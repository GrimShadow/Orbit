using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Shared.Content;

namespace Dam.Application.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    /// <summary>Identity-provider subject (the token `sub`). Stable; used as the audit actor.</summary>
    Guid? UserId { get; }
    string? Subject { get; }
    IReadOnlyCollection<string> Roles { get; }
}

public interface ITenantContext { Guid TenantId { get; } }

/// <summary>Single policy entry point (spec A7.2): RBAC + ABAC for the current user in the current tenant.</summary>
public interface IAuthorizationService
{
    /// <summary>May the current user do <paramref name="permission"/>, optionally on a specific resource?</summary>
    Task<bool> CanAsync(string permission, ResourceContext? resource = null, CancellationToken ct = default);

    /// <summary>The current user's access clauses for list filtering (search, collections). Empty means no access.</summary>
    Task<IReadOnlyList<ScopeClause>> GetScopeAsync(string permission, CancellationToken ct = default);

    Task<AccessProfile> GetProfileAsync(CancellationToken ct = default);
}

/// <summary>Builds the current user's <see cref="AccessProfile"/> (roles, groups, rules, attributes) from storage.</summary>
public interface IAccessProfileProvider
{
    Task<AccessProfile> GetAsync(CancellationToken ct);
}

public interface IUnitOfWork
{
    Task BeginAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

/// <summary>Collects events raised by aggregates touched in the current scope.</summary>
public interface IDomainEventSource { IReadOnlyList<DomainEvent> Drain(); }

public interface IOutbox { Task EnqueueAsync(IEnumerable<DomainEvent> events, CancellationToken ct); }

/// <summary>Lets handlers raise events that are not tied to a tracked aggregate. Drained by the outbox behaviour.</summary>
public interface IEventCollector { void Raise(DomainEvent e); }

/// <summary>Whether an asset has completed whatever approval workflow applies to it (spec Phase 2.3 "Workflow engine": Create →
/// Review → Approve → Publish). No workflow engine exists yet, so this is a seam: publishing's guard reads it today and will
/// need no change once a real workflow lands. The default implementation always answers yes.</summary>
public interface IWorkflowGate { Task<bool> IsApprovedAsync(Guid assetId, CancellationToken ct); }

/// <summary>Work that runs on a schedule, resolved by <see cref="Key"/> when its trigger fires (host: Dam.Scheduler).</summary>
public interface IScheduledJob
{
    string Key { get; }
    Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct);
}

public interface IJobScheduler
{
    /// <summary>Fire <paramref name="jobKey"/> once. Re-scheduling the same <paramref name="scheduleId"/> replaces the trigger.</summary>
    Task ScheduleOnceAsync(string scheduleId, string jobKey, DateTimeOffset at, IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default);
    Task ScheduleCronAsync(string scheduleId, string jobKey, string cron, IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default);
    Task<bool> CancelAsync(string scheduleId, CancellationToken ct = default);
}

/// <summary>Appends usage events and keeps each asset's usage summary (<c>asset_usage</c>) in step, atomically, inside the
/// caller's transaction. Done in one statement rather than through the change tracker because many requests record events for
/// the same hot assets at once: counters must be incremented in the database, and a re-sent clientEventId must lose a race
/// quietly instead of failing the batch.</summary>
public interface IUsageStore
{
    /// <returns>How many of <paramref name="events"/> were stored; the rest repeated an already-recorded clientEventId.</returns>
    Task<int> AppendAsync(IReadOnlyList<Dam.Domain.Assets.UsageEvent> events, CancellationToken ct);
}

/// <summary>Generic aggregate access. Handlers query with LINQ and materialise through <see cref="IQueryExecutor"/>.</summary>
public interface IRepository<T> where T : class
{
    IQueryable<T> Query();
    Task<T?> FindAsync(Guid id, CancellationToken ct);
    void Add(T entity);
    void AddRange(IEnumerable<T> entities);
    void Remove(T entity);
    void RemoveRange(IEnumerable<T> entities);
}

/// <summary>Async LINQ terminals, implemented by EF Core, so Application code never references EF.</summary>
public interface IQueryExecutor
{
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken ct);
    Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken ct);
    Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken ct);
    Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken ct);
}

/// <param name="LastModified">Some stores list every stored version of a re-sent part; the newest one is the current one.</param>
public sealed record StoredPart(int PartNumber, long Size, string ETag, DateTimeOffset? LastModified = null);

public sealed record ObjectInfo(long Size, string ETag);

/// <summary>
/// Object storage over the S3 API only (decision D5), so one code path serves AWS, Azure via a gateway, and on-prem stores.
/// File bytes never pass through the API: clients PUT parts straight to presigned URLs.
/// </summary>
public interface IObjectStore
{
    /// <summary>The untrusted landing bucket. Everything uploaded lands here until it has been scanned (Step 1.4).</summary>
    string QuarantineBucket { get; }
    string OriginalsBucket { get; }
    /// <summary>Derived files (renditions, transform cache). Safe to delete: everything here can be regenerated.</summary>
    string RenditionsBucket { get; }

    Task<string> StartMultipartAsync(string bucket, string key, string contentType, CancellationToken ct);
    /// <summary>A time-limited URL the browser PUTs one part to. Computed locally; no network call.</summary>
    string PresignPart(string bucket, string key, string s3UploadId, int partNumber, TimeSpan ttl);
    Task<IReadOnlyList<StoredPart>> ListPartsAsync(string bucket, string key, string s3UploadId, CancellationToken ct);
    Task<ObjectInfo> CompleteMultipartAsync(string bucket, string key, string s3UploadId, IEnumerable<StoredPart> parts, CancellationToken ct);
    Task AbortMultipartAsync(string bucket, string key, string s3UploadId, CancellationToken ct);
    Task<ObjectInfo?> HeadAsync(string bucket, string key, CancellationToken ct);
    Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken ct);
    /// <summary>Stores a whole (small) object in one request. Used for renditions, never for uploads.</summary>
    Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken ct);
    /// <summary>Reads <paramref name="length"/> bytes starting at <paramref name="offset"/> (fewer if the object is shorter).</summary>
    Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, long length, CancellationToken ct);
    /// <summary>Server-side copy between buckets. Objects above the store's single-copy limit are copied in parts.</summary>
    Task CopyAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, long size, CancellationToken ct);
    /// <summary>A URL for server-side tools (ffprobe) to read the object with range requests. Not for browsers.</summary>
    string PresignGet(string bucket, string key, TimeSpan ttl);
    Task DeleteAsync(string bucket, string key, CancellationToken ct);
}

/// <summary>Tunables for resumable uploads (env: DAM_UPLOAD_*).</summary>
public sealed class UploadSettings
{
    /// <summary>S3 requires every part but the last to be at least 5 MiB and allows at most 10,000 parts.</summary>
    public const int MinPartSize = 5 * 1024 * 1024;
    public const int MaxParts = 10_000;

    public int PartSizeBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>How long an unfinished upload stays resumable.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Presigned part URLs are short-lived (spec A7.6: at most 15 minutes); clients ask for fresh ones as they go.</summary>
    public TimeSpan PresignTtl { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>How many part URLs come back with the start of an upload.</summary>
    public int InitialPartUrls { get; set; } = 100;
    public int MaxPartUrlsPerRequest { get; set; } = 200;
}

public enum ScanVerdict { Clean, Infected, Skipped }

/// <param name="Signature">The malware signature name when infected.</param>
/// <param name="Note">Why a scan was skipped, or engine detail.</param>
public sealed record ScanOutcome(ScanVerdict Verdict, string Engine, string? Signature = null, string? Note = null);

/// <summary>Scans file content for malware (ClamAV). Throws when the scanner cannot be reached: ingest must fail closed.</summary>
public interface IVirusScanner
{
    Task<ScanOutcome> ScanAsync(Stream content, long length, CancellationToken ct);
}

/// <summary>Reads technical facts from audio and video (duration, codecs, resolution) without downloading the file.</summary>
public interface IMediaProbe
{
    /// <summary>Null when probing is unavailable (ffprobe not installed); throws when the file cannot be read as media.</summary>
    Task<System.Text.Json.JsonElement?> ProbeAsync(string bucket, string key, CancellationToken ct);
}

/// <summary>Delivery tunables (env: DAM_DELIVERY_*, DAM_CDN_BASE_URL). Links are built on the CDN address when there is one.</summary>
public sealed class DeliverySettings
{
    /// <summary>Where the delivery origin can be reached by clients when there is no CDN in front.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5290";
    /// <summary>When set, links point here instead: the CDN in front of the origin.</summary>
    public string? CdnBaseUrl { get; set; }
    /// <summary>Spec A7.6: download tokens last at most an hour unless the caller asks for longer, up to <see cref="MaxTokenTtl"/>.</summary>
    public TimeSpan DefaultTokenTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MaxTokenTtl { get; set; } = TimeSpan.FromHours(24);
    /// <summary>How long a CDN or browser may reuse a response, capped by the time left on the token.</summary>
    public int MaxAgeSeconds { get; set; } = 300;
    /// <summary>Images larger than this are not transformed on the fly.</summary>
    public long MaxSourceBytes { get; set; } = 200L * 1024 * 1024;
    /// <summary>A PDF with more pages than this only has its first pages rendered as "pdf_pages".</summary>
    public int MaxPdfPages { get; set; } = 200;

    public string LinkBase => (CdnBaseUrl is { Length: > 0 } cdn ? cdn : PublicBaseUrl).TrimEnd('/');
}

/// <param name="Text">Free-text query; null or empty means "browse" (filters/sort only, no relevance scoring).</param>
/// <param name="Filters">Exact-match filters, field to allowed values (OR within a field, AND across fields) - e.g.
/// <c>{"type": ["image"], "metadata.category.keyword": ["hero"]}</c>.</param>
/// <param name="Facets">Index fields to return value counts for alongside the hits (e.g. "status", "tags",
/// "metadata.category.keyword", "terms.path"). Nested (<c>terms.*</c>) fields are aggregated correctly automatically.</param>
/// <param name="Sort">"field" (ascending) or "-field" (descending); null or "_score" sorts by relevance. Unrecognised
/// fields fall back to relevance.</param>
/// <param name="SimilarTo">When set, finds assets like this one (title/metadata text) instead of using <paramref name="Text"/>.</param>
public sealed record SearchQuery(
    string? Text, IReadOnlyDictionary<string, string[]>? Filters, IReadOnlyList<string>? Facets, string? Sort,
    bool Highlight, Guid? SimilarTo, string? Cursor, int? Limit);

/// <summary>The search query surface (spec Step 1.7b), implemented against OpenSearch (decision D4). Every method takes
/// the caller's own <see cref="ScopeClause"/>s (from <see cref="IAuthorizationService.GetScopeAsync"/>) and pushes them
/// into the query as a filter, exactly like <c>ListAssetsHandler</c>'s in-memory clause check does today - a caller
/// with no matching clause sees nothing, never an error.</summary>
public interface ISearchQueryService
{
    Task<SearchResultDto> SearchAsync(SearchQuery query, IReadOnlyList<ScopeClause> scope, Guid userId, CancellationToken ct);

    /// <summary>Title matches for a partial query, for typeahead. Same scope enforcement as a full search.</summary>
    Task<IReadOnlyList<SuggestionDto>> SuggestAsync(string prefix, int limit, IReadOnlyList<ScopeClause> scope, Guid userId, CancellationToken ct);

    /// <summary>Value counts for <paramref name="fields"/> across every asset the caller may read, ignoring any query
    /// or filter (the whole visible corpus) - the standalone <c>GET /facets</c>, as opposed to <c>POST /search</c>'s
    /// own facets computed alongside a query's hits.</summary>
    Task<IReadOnlyList<FacetResultDto>> FacetsAsync(IReadOnlyList<string> fields, IReadOnlyList<ScopeClause> scope, Guid userId, CancellationToken ct);
}
