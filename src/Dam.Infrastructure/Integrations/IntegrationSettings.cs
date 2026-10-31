using Microsoft.Extensions.Configuration;

namespace Dam.Infrastructure.Integrations;

/// <summary>Connector tunables (env: DAM_INTEGRATIONS_*; the outbound address rules are the webhook ones, DAM_WEBHOOK_ALLOW_PRIVATE).</summary>
public sealed class IntegrationSettings
{
    /// <summary>The longest one call to another system may take (sign-in and API calls alike). Third-party sign-in is slower than a webhook receiver, so this is longer than the webhook timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How big a sign-in response is read (a token response is small; anything larger is not one).</summary>
    public int MaxLoginResponseBytes { get; set; } = 64 * 1024;

    /// <summary>How often a caller that is waiting for another caller's sign-in looks again.</summary>
    public TimeSpan LeasePollInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    public static IntegrationSettings Read(IConfiguration cfg) => new()
    {
        Timeout = TimeSpan.FromSeconds(int.TryParse(cfg["DAM_INTEGRATIONS_TIMEOUT_SECONDS"], out var t) && t is >= 1 and <= 60 ? t : 15),
    };
}
