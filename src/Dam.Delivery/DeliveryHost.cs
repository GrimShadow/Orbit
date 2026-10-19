using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Licensing;
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

        // Rights are looked at again now, not trusted from when the link was signed (ADR-0033): a licence can end, or its restrictions change, while a link is
        // still good. Ended is 410 like an expired asset; not yet begun, or a channel/region the link was issued for that the rights no longer allow, is 403.
        // The wording is deliberately generic: this endpoint is public, and the licence's name is not for link holders.
        var rights = asset.RightsId is { } rightsId ? await db.Rights.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rightsId, ct) : null;
        var check = RightsChecker.Check(rights, asset.Attributes, claims.Channel, claims.Region, RightsRules.Today(clock.UtcNow));
        if (check.Expired) return Problem(410, "rights_expired", "The licence for this asset has ended.");
        if (check.NotYetValid) return Problem(403, "rights_not_valid", "The licence for this asset has not begun.");
        if (!check.Allowed) return Problem(403, "rights_restricted", "This asset's rights do not allow that use.");

        // Unpublished content, and content whose rights require it, is only ever shown watermarked, whatever the link says.
        var watermark = claims.Watermark || asset.Status != AssetStatuses.Published || check.WatermarkRequired;

        // 4. Find or make the bytes.
        ServedObject? served;
        var service = scope.ServiceProvider.GetRequiredService<RenditionService>();
        try
        {
            if (transform is not null) served = await service.GetTransformAsync(version, transform, watermark, ct);
            else if (rendition == RenditionProfiles.Original)
            {
                if (watermark) return Problem(403, "invalid_link", "The original file of an unpublished or watermark-required asset cannot be downloaded through a link.");
                served = new ServedObject(version.Bucket, version.StorageKey, version.Mime, version.SizeBytes, CacheHit: true);
            }
            else served = await service.GetProfileAsync(version, rendition!, watermark, ct);
        }
        catch (UnsupportedImageException ex) { return Problem(422, "unsupported_image", ex.Message); }
        catch (SourceTooLargeException ex) { return Problem(422, "source_too_large", ex.Message); }
        catch (MediaToolMissingException) { return Problem(503, "renderer_unavailable", "This kind of file cannot be rendered right now."); }
        catch (MediaToolFailedException ex) { return Problem(422, "render_failed", ex.Message); }
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
        headers["Accept-Ranges"] = "bytes";      // a video player seeks by asking for a byte range; scrubbing needs this
        if (!served.Mime.StartsWith("image/", StringComparison.Ordinal) && served.Mime is not ("application/pdf" or "video/mp4" or "audio/mpeg"))
            headers.ContentDisposition = "attachment";            // never let an uploaded HTML file run on this origin

        if (http.Request.Headers.IfNoneMatch.Any(v => v == etag)) return Results.StatusCode(304);
        http.Response.ContentType = served.Mime;

        var store = sp.GetRequiredService<IObjectStore>();
        var range = ParseRange(http.Request.Headers.Range, served.Size);
        if (range is null)
        {
            http.Response.ContentLength = served.Size;
            if (HttpMethods.IsHead(http.Request.Method)) return Results.Empty;
            await using var whole = await store.OpenReadAsync(served.Bucket, served.Key, ct);
            await whole.CopyToAsync(http.Response.Body, ct);
            return Results.Empty;
        }

        if (range.Value.Start > range.Value.End)
        {
            headers["Content-Range"] = $"bytes */{served.Size}";
            return Results.StatusCode(416);
        }
        var length = range.Value.End - range.Value.Start + 1;
        http.Response.StatusCode = 206;
        headers["Content-Range"] = $"bytes {range.Value.Start}-{range.Value.End}/{served.Size}";
        http.Response.ContentLength = length;
        if (HttpMethods.IsHead(http.Request.Method)) return Results.Empty;
        var chunk = await store.ReadRangeAsync(served.Bucket, served.Key, range.Value.Start, length, ct);
        await http.Response.Body.WriteAsync(chunk, ct);
        return Results.Empty;
    }

    /// <summary>A single "bytes=start-end" range (the only form browsers and video players send). Anything else is ignored, which
    /// falls back to a full 200 response — a valid response to a Range request a server chooses not to honour.</summary>
    private static (long Start, long End)? ParseRange(StringValues header, long size)
    {
        var value = header.ToString();
        if (string.IsNullOrEmpty(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return null;
        var spec = value["bytes=".Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];
        if (startText.Length == 0)
        {
            // "bytes=-500": the last 500 bytes.
            if (!long.TryParse(endText, out var suffix) || suffix <= 0) return null;
            return (Math.Max(0, size - suffix), size - 1);
        }
        if (!long.TryParse(startText, out var start) || start < 0) return null;
        var end = endText.Length == 0 || !long.TryParse(endText, out var e) ? size - 1 : Math.Min(e, size - 1);
        return (start, end);
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
