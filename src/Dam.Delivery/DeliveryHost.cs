using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Infrastructure;
using Dam.Infrastructure.Delivery;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Dam.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

/// <summary>
/// The delivery origin (spec A3.1): sits behind the CDN and serves renditions and transforms for signed links. It never trusts the
/// caller; everything it may do is in the signed token.
/// </summary>
public static class DeliveryHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);       // first, so callers (tests) can supply configuration the rest of this method reads
        var cfg = builder.Configuration;
        string Need(string k) => cfg[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} is not set.");

        builder.Services.AddDamWorkerIdentity();     // no interactive user; the tenant comes from the token
        builder.Services.AddDamInfrastructure(Need("DAM_DB_CONNECTION"));
        builder.Services.AddDamStorage(cfg);
        builder.Services.AddDamDelivery(cfg);
        builder.Services.AddSingleton(new DeliveryOptions { TrustForwardedFor = string.Equals(cfg["DAM_DELIVERY_TRUST_FORWARDED"], "true", StringComparison.OrdinalIgnoreCase) });

        var app = builder.Build();
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapMethods("/d/{token}/{assetId:guid}/t", ["GET", "HEAD"], (string token, Guid assetId, HttpContext http) => Serve(http, token, assetId, null));
        app.MapMethods("/d/{token}/{assetId:guid}/{rendition}", ["GET", "HEAD"], (string token, Guid assetId, string rendition, HttpContext http) => Serve(http, token, assetId, rendition));
        return app;
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, type: $"https://orbit.example/problems/{code}");

    private static async Task<IResult> Serve(HttpContext http, string token, Guid assetId, string? rendition)
    {
        var sp = http.RequestServices;
        var settings = sp.GetRequiredService<DeliverySettings>();
        var clock = sp.GetRequiredService<IClock>();
        var ct = http.RequestAborted;

        // 1. The token: signature, expiry, and that it is for this asset.
        var (claims, failure) = sp.GetRequiredService<DeliveryTokenSigner>().Verify(token, clock.UtcNow);
        if (claims is null)
            return failure == TokenFailure.Expired ? Problem(410, "link_expired", "This link has expired.") : Problem(403, "invalid_link", "This link is not valid.");
        if (claims.AssetId != assetId) return Problem(403, "invalid_link", "This link is not valid for this asset.");
        if (claims.Ip is not null && ClientIp(http, sp.GetRequiredService<DeliveryOptions>()) != claims.Ip) return Problem(403, "invalid_link", "This link cannot be used from your address.");

        // 2. What was asked for must be exactly what was signed.
        TransformSpec? transform = null;
        if (rendition is null)
        {
            var q = http.Request.Query;
            // Only the signed parameters are allowed: anything else would give the CDN many URLs for one picture.
            if (q.Keys.Any(k => k is not ("w" or "h" or "fit" or "fmt" or "q" or "focal"))) return Problem(403, "invalid_link", "The parameters do not match the signed link.");
            var (spec, error) = TransformSpec.Parse(q["w"], q["h"], q["fit"], q["fmt"], q["q"], q["focal"]);
            if (spec is null) return Problem(400, "invalid_transform", error!);
            if (claims.TransformScope != spec.Canonical()) return Problem(403, "invalid_link", "The parameters do not match the signed link.");
            transform = spec;
        }
        else if (!claims.AllowsRendition(rendition)) return Problem(403, "invalid_link", "This link does not allow that rendition.");

        // 3. Load the asset as its own tenant (RLS applies), and check it may be served at all.
        await using var scope = sp.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = claims.TenantId;
        var db = scope.ServiceProvider.GetRequiredService<DamDbContext>();
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null || asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked || asset.CurrentVersionId is null) return Problem(404, "not_found", "Not found.");
        if (asset.Status is AssetStatuses.Expired or AssetStatuses.Archived) return Problem(410, "gone", "This asset is no longer available.");
        var version = await db.AssetVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == asset.CurrentVersionId, ct);
        if (version is null) return Problem(404, "not_found", "Not found.");

        // Unpublished content is only ever shown watermarked, whatever the link says.
        var watermark = claims.Watermark || asset.Status != AssetStatuses.Published;

        // 4. Find or make the bytes.
        ServedObject? served;
        var service = scope.ServiceProvider.GetRequiredService<RenditionService>();
        try
        {
            if (transform is not null) served = await service.GetTransformAsync(version, transform, watermark, ct);
            else if (rendition == RenditionProfiles.Original)
            {
                if (watermark) return Problem(403, "invalid_link", "The original file of an unpublished asset cannot be downloaded through a link.");
                served = new ServedObject(version.Bucket, version.StorageKey, version.Mime, version.SizeBytes, CacheHit: true);
            }
            else served = RenditionProfiles.Find(rendition!) is { } profile ? await service.GetProfileAsync(version, profile, watermark, ct) : null;
        }
        catch (UnsupportedImageException ex) { return Problem(422, "unsupported_image", ex.Message); }
        catch (SourceTooLargeException ex) { return Problem(422, "source_too_large", ex.Message); }
        if (served is null) return Problem(404, "rendition_not_available", "This kind of file has no such rendition.");

        // 5. Send it, with what a CDN needs to cache it correctly.
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(served.Key)))[..24]}\"";
        var remaining = (int)Math.Max(0, (claims.ExpiresAt - clock.UtcNow).TotalSeconds);
        var maxAge = Math.Min(settings.MaxAgeSeconds, remaining);
        var headers = http.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = watermark ? $"private, max-age={maxAge}" : $"public, max-age={maxAge}";   // unreleased content must not sit in a shared cache
        headers["X-Dam-Cache"] = served.CacheHit ? "hit" : "miss";
        headers["X-Dam-Version"] = version.Id.ToString("N");
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        if (!served.Mime.StartsWith("image/", StringComparison.Ordinal) && served.Mime is not ("application/pdf" or "video/mp4" or "audio/mpeg"))
            headers.ContentDisposition = "attachment";            // never let an uploaded HTML file run on this origin

        if (http.Request.Headers.IfNoneMatch.Any(v => v == etag)) return Results.StatusCode(304);
        http.Response.ContentType = served.Mime;
        http.Response.ContentLength = served.Size;
        if (HttpMethods.IsHead(http.Request.Method)) return Results.Empty;

        var store = sp.GetRequiredService<IObjectStore>();
        await using var body = await store.OpenReadAsync(served.Bucket, served.Key, ct);
        await body.CopyToAsync(http.Response.Body, ct);
        return Results.Empty;
    }

    private static string? ClientIp(HttpContext http, DeliveryOptions options)
    {
        if (options.TrustForwardedFor && http.Request.Headers.TryGetValue("X-Forwarded-For", out StringValues forwarded) && forwarded.ToString().Split(',')[0].Trim() is { Length: > 0 } first)
            return IPAddress.TryParse(first, out var ip) ? ip.ToString() : null;
        return http.Connection.RemoteIpAddress?.MapToIPv4().ToString();
    }
}

/// <summary>Behind a CDN or ingress the caller's address is in X-Forwarded-For. Trust it only when a proxy we control sets it.</summary>
public sealed class DeliveryOptions { public bool TrustForwardedFor { get; set; } }
