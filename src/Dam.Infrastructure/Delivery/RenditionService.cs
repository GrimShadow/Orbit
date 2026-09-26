using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure.Delivery;

/// <summary>An object in storage that can be sent to a client as-is.</summary>
public sealed record ServedObject(string Bucket, string Key, string Mime, long Size, bool CacheHit);

public sealed class SourceTooLargeException(long size, long limit) : Exception($"The source is {size} bytes; on-the-fly rendering is limited to {limit}.");

/// <summary>
/// Produces renditions and transforms from an original, storing each under a deterministic key so the same request is only ever
/// rendered once (spec A3.3 / Step 1.5). Used by the worker (eager profiles) and by the delivery origin (on demand).
/// </summary>
public sealed class RenditionService(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, DeliverySettings settings)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    /// <summary>True for versions we can rasterise. SVG is left alone: it can reference external files, and it scales anyway.</summary>
    public static bool CanRender(AssetVersion v) => v.Mime.StartsWith("image/", StringComparison.Ordinal) && v.Mime != "image/svg+xml";

    /// <summary>The named profile for a version: reuses the stored one, otherwise renders and stores it.</summary>
    public async Task<ServedObject?> GetProfileAsync(AssetVersion version, RenditionProfile profile, bool watermark, CancellationToken ct)
    {
        if (!CanRender(version)) return null;
        if (watermark) return await GetCachedAsync(version, $"x/{Hash($"profile:{profile.Name}|wm")}", RenderRequest.ForProfile(profile, watermark: true), profile.Format, ct);

        var existing = await db.Renditions.AsNoTracking().FirstOrDefaultAsync(r => r.VersionId == version.Id && r.Profile == profile.Name && r.Status == RenditionStatus.Ready, ct);
        if (existing is not null && await store.HeadAsync(existing.Bucket, existing.StorageKey, ct) is not null)
            return new ServedObject(existing.Bucket, existing.StorageKey, existing.Mime, existing.SizeBytes, CacheHit: true);
        return await GenerateProfileAsync(version, profile, ct);
    }

    /// <summary>Renders and records one profile. Safe to repeat: the same version and profile map to the same key and row.</summary>
    public async Task<ServedObject?> GenerateProfileAsync(AssetVersion version, RenditionProfile profile, CancellationToken ct)
    {
        if (!CanRender(version)) return null;
        var key = Rendition.KeyFor(version.TenantId, version.AssetId, version.Id, profile.Name, RenditionProfiles.Extension(profile.Format));
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var bucket = store.RenditionsBucket;
            var rendered = ImageRenderer.Render(await ReadSourceAsync(version, ct), RenderRequest.ForProfile(profile));
            await store.PutAsync(bucket, key, rendered.Bytes, rendered.Mime, ct);

            var row = await db.Renditions.FirstOrDefaultAsync(r => r.VersionId == version.Id && r.Profile == profile.Name, ct);
            if (row is not null) db.Renditions.Remove(row);      // replace a stale or failed row with the fresh one
            if (row is not null) await db.SaveChangesAsync(ct);
            db.Renditions.Add(Rendition.Create(version.TenantId, version.AssetId, version.Id, profile.Name, bucket, key, rendered.Mime, rendered.Width, rendered.Height, rendered.Bytes.Length, clock.UtcNow));
            await outbox.EnqueueAsync([new RenditionReady(version.AssetId, version.Id, profile.Name)], ct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* another request stored the same rendition first; the object is identical */ db.ChangeTracker.Clear(); }
            return new ServedObject(bucket, key, rendered.Mime, rendered.Bytes.Length, CacheHit: false);
        }
        finally { gate.Release(); }
    }

    /// <summary>A one-off transform (or watermarked variant), cached by a hash of exactly what was asked for.</summary>
    public Task<ServedObject?> GetTransformAsync(AssetVersion version, TransformSpec spec, bool watermark, CancellationToken ct) =>
        CanRender(version)
            ? GetCachedAsync(version, $"x/{Hash($"transform:{spec.Canonical()}|wm:{watermark}")}", RenderRequest.ForTransform(spec, watermark), spec.Format, ct)
            : Task.FromResult<ServedObject?>(null);

    private async Task<ServedObject?> GetCachedAsync(AssetVersion version, string keyTail, RenderRequest request, string format, CancellationToken ct)
    {
        var key = $"{version.TenantId:N}/{version.AssetId:N}/{version.Id:N}/{keyTail}.{RenditionProfiles.Extension(format)}";
        var bucket = store.RenditionsBucket;
        var head = await store.HeadAsync(bucket, key, ct);
        if (head is not null) return new ServedObject(bucket, key, RenditionProfiles.MimeOf(format), head.Size, CacheHit: true);

        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            head = await store.HeadAsync(bucket, key, ct);                  // another request may have finished while we waited
            if (head is not null) return new ServedObject(bucket, key, RenditionProfiles.MimeOf(format), head.Size, CacheHit: true);
            var rendered = ImageRenderer.Render(await ReadSourceAsync(version, ct), request);
            await store.PutAsync(bucket, key, rendered.Bytes, rendered.Mime, ct);
            return new ServedObject(bucket, key, rendered.Mime, rendered.Bytes.Length, CacheHit: false);
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadSourceAsync(AssetVersion version, CancellationToken ct)
    {
        if (version.SizeBytes > settings.MaxSourceBytes) throw new SourceTooLargeException(version.SizeBytes, settings.MaxSourceBytes);
        await using var s = await store.OpenReadAsync(version.Bucket, version.StorageKey, ct);
        using var ms = new MemoryStream((int)Math.Min(version.SizeBytes, int.MaxValue));
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];
}

public static class DeliveryRegistration
{
    /// <summary>Renditions only (the worker's need): no signing key required.</summary>
    public static IServiceCollection AddDamRendering(this IServiceCollection services, IConfiguration cfg)
    {
        var settings = new DeliverySettings();
        if (cfg["DAM_DELIVERY_PUBLIC_BASE_URL"] is { Length: > 0 } pub) settings.PublicBaseUrl = pub;
        if (cfg["DAM_CDN_BASE_URL"] is { Length: > 0 } cdn) settings.CdnBaseUrl = cdn;
        if (int.TryParse(cfg["DAM_DELIVERY_TOKEN_TTL_MINUTES"], out var ttl) && ttl > 0) settings.DefaultTokenTtl = TimeSpan.FromMinutes(ttl);
        if (int.TryParse(cfg["DAM_DELIVERY_TOKEN_MAX_MINUTES"], out var max) && max > 0) settings.MaxTokenTtl = TimeSpan.FromMinutes(max);
        if (settings.DefaultTokenTtl > settings.MaxTokenTtl) settings.DefaultTokenTtl = settings.MaxTokenTtl;
        if (int.TryParse(cfg["DAM_DELIVERY_MAX_AGE_SECONDS"], out var age) && age >= 0) settings.MaxAgeSeconds = age;
        if (long.TryParse(cfg["DAM_RENDER_MAX_SOURCE_MB"], out var mb) && mb > 0) settings.MaxSourceBytes = mb * 1024 * 1024;
        services.AddSingleton(settings);
        services.AddScoped<RenditionService>();
        return services;
    }

    /// <summary>Everything the API and the delivery origin need: rendering plus the token signer (DAM_DELIVERY_SIGNING_KEY, comma-separated to rotate).</summary>
    public static IServiceCollection AddDamDelivery(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDamRendering(cfg);
        var keys = (cfg["DAM_DELIVERY_SIGNING_KEY"] ?? throw new InvalidOperationException("DAM_DELIVERY_SIGNING_KEY is not set (at least 32 characters; separate several with commas to rotate).")).Split(',');
        services.AddSingleton(new DeliveryTokenSigner(keys));
        return services;
    }
}
