using System.Net;
using System.Net.Sockets;

namespace Dam.Domain.Webhooks;

/// <summary>Retry schedule for failed deliveries: exponential steps with jitter, then give up.</summary>
/// <param name="MaxAttempts">Total attempts (the first one included) before a delivery is marked failed.</param>
/// <param name="Delays">Wait after failed attempt 1, 2, 3...; the last step repeats if there are more attempts than steps.</param>
/// <param name="Jitter">Each wait is scaled by a random factor in [1 - Jitter, 1 + Jitter], so many failures do not retry in lockstep.</param>
public sealed record WebhookRetryPolicy(int MaxAttempts, IReadOnlyList<TimeSpan> Delays, double Jitter)
{
    /// <summary>10 s, 1 min, 5 min, 30 min, 2 h, 6 h, 12 h: eight attempts over about 21 hours.</summary>
    public static readonly WebhookRetryPolicy Default = new(8,
        [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
         TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12)], 0.2);

    /// <summary>The wait before the next attempt after <paramref name="failedAttempt"/> (1-based) failed, or null when it was the last.</summary>
    /// <param name="random">Returns a value in [0, 1); supplied by tests to make jitter deterministic.</param>
    public TimeSpan? DelayAfter(int failedAttempt, Func<double> random)
    {
        if (failedAttempt >= MaxAttempts || Delays.Count == 0) return null;
        var baseDelay = Delays[Math.Min(failedAttempt, Delays.Count) - 1];
        var factor = 1 + ((random() * 2) - 1) * Jitter;
        return TimeSpan.FromSeconds(Math.Round(baseDelay.TotalSeconds * factor, 3));
    }
}

public static class DeliveryStatuses
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public static readonly IReadOnlyList<string> All = [Pending, Succeeded, Failed];
}

/// <summary>
/// Which network addresses a webhook may call. Server-side request forgery is the risk: a tenant admin points a webhook at an
/// internal service or a cloud metadata endpoint. Loopback, private, link-local (which includes 169.254.169.254), carrier-grade NAT,
/// multicast, reserved and documentation ranges are refused, for IPv4, IPv6 and IPv6 forms that embed an IPv4 address.
/// </summary>
public static class AddressGuard
{
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsBlocked(address.MapToIPv4());
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsBlockedV4(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;

        var b = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;   // fe80::/10, fec0::/10, ff00::/8
        if ((b[0] & 0xFE) == 0xFC) return true;                                                           // fc00::/7 unique local (AWS metadata fd00:ec2::254 is here)
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;                 // 2001:db8::/32 documentation
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))   // 64:ff9b::/96 NAT64: judge the embedded IPv4
            return IsBlockedV4(b[12..16]);
        if (b[..12].All(x => x == 0)) return IsBlockedV4(b[12..16]);                                      // ::a.b.c.d (deprecated IPv4-compatible)
        if (b[0] == 0x20 && b[1] == 0x02) return IsBlockedV4(b[2..6]);                                    // 2002::/16 6to4: embedded IPv4
        return false;
    }

    private static bool IsBlockedV4(byte[] b) =>
        b[0] == 0                                          // 0.0.0.0/8 "this network"
        || b[0] == 10                                      // 10/8
        || (b[0] == 100 && b[1] is >= 64 and <= 127)       // 100.64/10 carrier-grade NAT
        || b[0] == 127                                     // loopback
        || (b[0] == 169 && b[1] == 254)                    // link-local, includes the cloud metadata address
        || (b[0] == 172 && b[1] is >= 16 and <= 31)        // 172.16/12
        || (b[0] == 192 && b[1] == 0 && b[2] == 0)         // 192.0.0.0/24 IETF protocol assignments
        || (b[0] == 192 && b[1] == 0 && b[2] == 2)         // documentation
        || (b[0] == 192 && b[1] == 168)                    // 192.168/16
        || (b[0] == 198 && b[1] is 18 or 19)               // 198.18/15 benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100)      // documentation
        || (b[0] == 203 && b[1] == 0 && b[2] == 113)       // documentation
        || b[0] >= 224;                                    // multicast, reserved, broadcast

    /// <summary>Syntax checks that need no network: absolute http(s) URL, a host, no credentials in the URL, a sane length.</summary>
    public static string? CheckSyntax(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "A URL is required.";
        if (url.Length > 2000) return "The URL is too long (at most 2000 characters).";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "Enter a full URL such as https://example.com/hook.";
        if (uri.Scheme is not ("http" or "https")) return "Only http and https URLs are allowed.";
        if (uri.UserInfo.Length > 0) return "The URL must not contain a user name or password.";
        if (uri.Host.Length == 0) return "The URL has no host.";
        return null;
    }
}
