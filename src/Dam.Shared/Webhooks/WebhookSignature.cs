using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dam.Shared.Webhooks;

/// <summary>Why a signature check passed or failed (see <see cref="WebhookSignature.Verify"/>).</summary>
public enum WebhookVerification { Valid, MissingHeaders, MalformedTimestamp, OutsideTolerance, BadSignature }

/// <summary>
/// The signing scheme of Orbit webhooks (ADR-0031). Receivers verify a delivery by recomputing
/// <c>HMAC-SHA256(secret, timestamp + "." + rawBody)</c>, comparing it to the <c>X-Dam-Signature</c> header (<c>v1=&lt;hex&gt;</c>) in constant
/// time, and refusing timestamps older (or newer) than a few minutes so a captured delivery cannot be replayed later. The key is the
/// secret string exactly as shown once at creation (including its <c>whsec_</c> prefix), as UTF-8. Lives in the shared assembly so the
/// .NET SDK and integrations can use the very same code the server signs with.
/// </summary>
public static class WebhookSignature
{
    public const string EventHeader = "X-Dam-Event";
    public const string DeliveryHeader = "X-Dam-Delivery";
    public const string TimestampHeader = "X-Dam-Timestamp";
    public const string SignatureHeader = "X-Dam-Signature";
    public const string Version = "v1";
    public const string SecretPrefix = "whsec_";

    /// <summary>The tolerance the documentation recommends receivers use.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>A new signing secret: <c>whsec_</c> + 32 random bytes, base64url.</summary>
    public static string GenerateSecret() => SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The header value for a body sent at <paramref name="unixSeconds"/>: <c>v1=</c> and lowercase hex.</summary>
    public static string Sign(string secret, long unixSeconds, ReadOnlySpan<byte> body) => $"{Version}={Convert.ToHexStringLower(Mac(secret, unixSeconds, body))}";

    public static string Sign(string secret, long unixSeconds, string body) => Sign(secret, unixSeconds, Encoding.UTF8.GetBytes(body));

    /// <summary>
    /// Checks a delivery. <paramref name="signatureHeader"/> may list several comma- or space-separated <c>v1=</c> values (a sender may
    /// sign with two secrets while one is being rotated); any match is enough. The timestamp must be within <paramref name="tolerance"/>
    /// of <paramref name="now"/> in either direction.
    /// </summary>
    public static WebhookVerification Verify(string secret, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body,
        DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader)) return WebhookVerification.MissingHeaders;
        if (!long.TryParse(timestampHeader.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) return WebhookVerification.MalformedTimestamp;
        // Check the signature before the clock so that a wrong secret is never reported as "stale".
        var expected = Mac(secret, ts, body);
        var ok = false;
        foreach (var candidate in signatureHeader.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!candidate.StartsWith(Version + "=", StringComparison.Ordinal)) continue;
            var hex = candidate[(Version.Length + 1)..];
            if (hex.Length != expected.Length * 2) continue;
            byte[] given;
            try { given = Convert.FromHexString(hex); } catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(given, expected)) ok = true;   // keep looping: constant work per candidate
        }
        if (!ok) return WebhookVerification.BadSignature;
        var skew = (now - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration();
        return skew <= tolerance ? WebhookVerification.Valid : WebhookVerification.OutsideTolerance;
    }

    public static WebhookVerification Verify(string secret, string? timestampHeader, string? signatureHeader, string body, DateTimeOffset now, TimeSpan tolerance) =>
        Verify(secret, timestampHeader, signatureHeader, Encoding.UTF8.GetBytes(body), now, tolerance);

    private static byte[] Mac(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
