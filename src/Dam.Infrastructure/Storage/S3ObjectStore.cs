using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Dam.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure.Storage;

public sealed class S3Options
{
    /// <summary>Where the server talks to storage (e.g. the compose service name).</summary>
    public string Endpoint { get; set; } = "http://localhost:9000";
    /// <summary>Where BROWSERS reach storage. Presigned URLs are signed for this host. Defaults to <see cref="Endpoint"/>.</summary>
    public string? PublicEndpoint { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string BucketOriginals { get; set; } = "dam-originals";
    public string BucketRenditions { get; set; } = "dam-renditions";
    public string BucketQuarantine { get; set; } = "dam-quarantine";
}

/// <summary>
/// S3-API object store (decision D5). Uses two clients: one for calls the server makes, and one used only to sign URLs for
/// the address browsers use, because a presigned URL is bound to the host it was signed for.
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _signer;
    private readonly S3Options _options;
    private readonly Protocol _publicScheme;

    public S3ObjectStore(S3Options options)
    {
        _options = options;
        _client = Create(options.Endpoint, options);
        _signer = Create(options.PublicEndpoint ?? options.Endpoint, options);
        _publicScheme = new Uri(options.PublicEndpoint ?? options.Endpoint).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
    }

    private static AmazonS3Client Create(string endpoint, S3Options o) =>
        new(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = o.Region,
            // Third-party S3 stores do not implement the newer default checksum headers; only send them when required.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public string QuarantineBucket => _options.BucketQuarantine;
    public string OriginalsBucket => _options.BucketOriginals;

    public async Task<string> StartMultipartAsync(string bucket, string key, string contentType, CancellationToken ct)
    {
        var r = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ContentType = contentType }, ct);
        return r.UploadId;
    }

    public string PresignPart(string bucket, string key, string s3UploadId, int partNumber, TimeSpan ttl) =>
        _signer.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket, Key = key, Verb = HttpVerb.PUT, UploadId = s3UploadId, PartNumber = partNumber,
            Expires = DateTime.UtcNow.Add(ttl),
            // The SDK defaults presigned URLs to HTTPS whatever the endpoint is; follow the configured endpoint's scheme instead.
            Protocol = _publicScheme,
        });

    public async Task<IReadOnlyList<StoredPart>> ListPartsAsync(string bucket, string key, string s3UploadId, CancellationToken ct)
    {
        var parts = new List<StoredPart>();
        int? marker = null;
        while (true)
        {
            var r = await _client.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = s3UploadId, PartNumberMarker = marker?.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
            parts.AddRange((r.Parts ?? []).Select(p => new StoredPart(p.PartNumber ?? 0, p.Size ?? 0, p.ETag ?? "", p.LastModified is { } lm ? new DateTimeOffset(lm, TimeSpan.Zero) : null)));
            if (r.IsTruncated != true) break;
            marker = r.NextPartNumberMarker;
        }
        return parts;
    }

    public async Task<ObjectInfo> CompleteMultipartAsync(string bucket, string key, string s3UploadId, IEnumerable<StoredPart> parts, CancellationToken ct)
    {
        var r = await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = key, UploadId = s3UploadId,
            PartETags = parts.Select(p => new PartETag(p.PartNumber, p.ETag)).ToList(),
        }, ct);
        var head = await HeadAsync(bucket, key, ct);
        return new ObjectInfo(head?.Size ?? 0, r.ETag ?? "");
    }

    public async Task AbortMultipartAsync(string bucket, string key, string s3UploadId, CancellationToken ct)
    {
        try { await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = s3UploadId }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload") { /* already gone */ }
    }

    public async Task<ObjectInfo?> HeadAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct);
            return new ObjectInfo(r.ContentLength, r.ETag ?? "");
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken ct)
    {
        var r = await _client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct);
        return new OwningStream(r.ResponseStream, r);
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken ct) =>
        _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);

    public void Dispose() { _client.Dispose(); _signer.Dispose(); }

    /// <summary>Keeps the S3 response alive while its stream is read, then disposes both.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

public static class StorageRegistration
{
    /// <summary>Reads the DAM_S3_* and DAM_UPLOAD_* settings (spec Appendix A).</summary>
    public static IServiceCollection AddDamStorage(this IServiceCollection services, IConfiguration cfg)
    {
        string Need(string key) => cfg[key] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{key} is not set.");
        var s3 = new S3Options
        {
            Endpoint = Need("DAM_S3_ENDPOINT"),
            PublicEndpoint = cfg["DAM_S3_PUBLIC_ENDPOINT"],
            Region = cfg["DAM_S3_REGION"] ?? "us-east-1",
            AccessKey = Need("DAM_S3_ACCESS_KEY"),
            SecretKey = Need("DAM_S3_SECRET_KEY"),
            BucketOriginals = cfg["DAM_S3_BUCKET_ORIGINALS"] ?? "dam-originals",
            BucketRenditions = cfg["DAM_S3_BUCKET_RENDITIONS"] ?? "dam-renditions",
            BucketQuarantine = cfg["DAM_S3_BUCKET_QUARANTINE"] ?? "dam-quarantine",
        };
        var uploads = new UploadSettings();
        if (int.TryParse(cfg["DAM_UPLOAD_PART_SIZE_MB"], out var mb) && mb > 0)
            uploads.PartSizeBytes = Math.Max(UploadSettings.MinPartSize, mb * 1024 * 1024);
        if (int.TryParse(cfg["DAM_UPLOAD_SESSION_HOURS"], out var hours) && hours > 0) uploads.SessionLifetime = TimeSpan.FromHours(hours);
        if (uploads.PartSizeBytes < UploadSettings.MinPartSize) uploads.PartSizeBytes = UploadSettings.MinPartSize;

        services.AddSingleton(s3);
        services.AddSingleton(uploads);
        services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(s3));
        return services;
    }
}
