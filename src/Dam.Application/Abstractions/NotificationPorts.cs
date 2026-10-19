using Dam.Domain.Webhooks;
using Dam.Shared.Audit;

namespace Dam.Application.Abstractions;

/// <summary>Encrypts small secrets at rest (AES-GCM under <c>DAM_SECRETS_KEY</c>): webhook signing secrets and chat webhook URLs.</summary>
public interface ISecretProtector
{
    /// <param name="purpose">Bound into the ciphertext, so a value protected for one use cannot be decrypted as another.</param>
    string Protect(string plaintext, string purpose);
    string Unprotect(string protectedValue, string purpose);
}

public sealed record EmailMessage(string ToAddress, string? ToName, string Subject, string Html, string Text);

/// <summary>Sends email. When no SMTP server is configured <see cref="IsEnabled"/> is false and nothing is sent.</summary>
public interface IEmailSender
{
    bool IsEnabled { get; }
    /// <summary>Throws when the message could not be handed to the server, so the caller's retry policy applies.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Decides whether a webhook or chat URL may be called: http(s) only and, unless private targets are allowed, only public addresses.</summary>
public interface IOutboundUrlGuard
{
    /// <summary>Null when the URL is acceptable, else a message for the user. Resolves the host, so call it when saving, not on hot paths.</summary>
    Task<string?> CheckAsync(string url, CancellationToken ct);
}

/// <summary>Makes (or retries) one webhook delivery attempt and records the outcome. Runs inside a tenant-scoped scope.</summary>
public interface IWebhookDeliverer
{
    /// <summary>Attempts the delivery if it is due (or brand new); does nothing when it already succeeded, failed, or another worker has it.</summary>
    Task AttemptAsync(Guid deliveryId, CancellationToken ct);
}

public sealed record ChatMessage(string Title, string Body, string? LinkUrl);

public sealed record ChatPostResult(bool Ok, int? StatusCode, string? Error, int DurationMs);

/// <summary>Posts a small message to a Slack or Teams incoming webhook.</summary>
public interface IChatSender
{
    Task<ChatPostResult> SendAsync(string kind, string url, ChatMessage message, CancellationToken ct);
}

/// <summary>Webhook tunables (env: DAM_WEBHOOK_*).</summary>
public sealed class WebhookSettings
{
    /// <summary>Allow targets on private, loopback and link-local addresses. Off by default (SSRF); on in development.</summary>
    public bool AllowPrivate { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public WebhookRetryPolicy Retry { get; set; } = WebhookRetryPolicy.Default;
    /// <summary>How long a freshly created delivery is reserved for its creator's immediate attempt before the retry job may take it.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);
    public int ResponseExcerptBytes { get; set; } = 2048;
}

/// <summary>Notification tunables (env: DAM_CONSOLE_PUBLIC_URL).</summary>
public sealed class NotificationSettings
{
    /// <summary>Where the console is reachable from outside, for absolute links in email. Null: emails carry the path only.</summary>
    public string? ConsolePublicUrl { get; set; }
}

public sealed record AuditFilter(
    DateTimeOffset? From, DateTimeOffset? To, string? Actor, string? ActionPrefix, string? EntityType, Guid? EntityId, string? Text);

public sealed record AuditPage(IReadOnlyList<AuditEntryDto> Items, long? NextBefore);

/// <summary>A consistent read of the caller's tenant's audit log: everything it returns comes from one snapshot, so an export's header
/// and body agree even while new rows are being appended.</summary>
public interface IAuditSnapshot : IAsyncDisposable
{
    /// <summary>Newest first, strictly below <paramref name="beforeChainNo"/> when given.</summary>
    Task<AuditPage> ListAsync(AuditFilter filter, long? beforeChainNo, int limit, CancellationToken ct);

    /// <summary>Oldest first, one row at a time (never buffers the range).</summary>
    IAsyncEnumerable<AuditEntryDto> StreamAsync(AuditFilter filter, CancellationToken ct);

    /// <summary>Rows after a chain position, oldest first; used by the SIEM forwarder.</summary>
    Task<IReadOnlyList<AuditEntryDto>> ReadAfterAsync(long afterChainNo, int limit, CancellationToken ct);

    Task<long> MaxChainNoAsync(CancellationToken ct);
    Task<long> CountAsync(AuditFilter filter, CancellationToken ct);

    /// <summary>Recomputes the hash chain over the rows whose time falls in the range (and every row between them).</summary>
    Task<AuditVerificationDto> VerifyAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>Distinct action names recorded, for filter pickers.</summary>
    Task<IReadOnlyList<string>> ActionsAsync(CancellationToken ct);
}

public interface IAuditReader
{
    /// <summary>Opens a read-only snapshot scoped to the current tenant.</summary>
    Task<IAuditSnapshot> OpenAsync(CancellationToken ct);
}
