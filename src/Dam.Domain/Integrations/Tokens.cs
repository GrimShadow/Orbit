using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dam.Domain.Integrations;

/// <summary>The outcome of reading a sign-in or token response: a token and how long it lasts, or a message (which never contains the response body).</summary>
public sealed record TokenResponse(string? Token, int LifetimeSeconds, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>How long a fetched token is believed to last, and when it may still be reused (ADR-0040 decision 4).</summary>
public static class TokenLifetime
{
    /// <summary>Used when the response gives no usable lifetime and none is configured. A token that dies earlier costs one 401 and one new sign-in.</summary>
    public const int DefaultSeconds = 300;
    /// <summary>Smallest fixed lifetime a connection may configure.</summary>
    public const int MinFixedSeconds = 60;
    /// <summary>Largest lifetime believed, however large the response says: a bogus value must not keep a dead token for days.</summary>
    public const int MaxSeconds = 86_400;
    /// <summary>A token is reused until this long before it ends.</summary>
    public static readonly TimeSpan ReuseMargin = TimeSpan.FromSeconds(60);

    /// <summary>The lifetime in seconds: the value the response gave (a positive number, or text holding one), else the configured fixed lifetime, else <see cref="DefaultSeconds"/>; at most <see cref="MaxSeconds"/>.</summary>
    public static int Resolve(JsonElement? fromResponse, int? fixedSeconds)
    {
        if (fromResponse is { } e && ReadSeconds(e) is { } s and > 0) return (int)Math.Min(s, MaxSeconds);
        if (fixedSeconds is { } f and > 0) return Math.Clamp(f, 1, MaxSeconds);
        return DefaultSeconds;
    }

    private static double? ReadSeconds(JsonElement e)
    {
        double v;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number when e.TryGetDouble(out v): break;
            case JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v): break;
            default: return null;
        }
        return double.IsFinite(v) ? Math.Floor(v) : null;
    }

    public static DateTimeOffset ExpiresAt(DateTimeOffset now, int lifetimeSeconds) => now.AddSeconds(lifetimeSeconds);

    /// <summary>True while the token is more than <see cref="ReuseMargin"/> from its end.</summary>
    public static bool IsUsable(DateTimeOffset? expiresAt, DateTimeOffset now) => expiresAt is { } end && end - now > ReuseMargin;
}

/// <summary>Reads a token out of a response body by JSON pointer.</summary>
public static class TokenExtraction
{
    public const int MaxTokenLength = 8192;

    public static TokenResponse Read(string body, string tokenPointer, string? expiryPointer, int? fixedSeconds)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return Fail("The sign-in response is not valid JSON."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!JsonPointer.TryResolve(root, tokenPointer, out var tokenElement)) return Fail($"The sign-in response has no value at {tokenPointer}.");
            if (tokenElement.ValueKind != JsonValueKind.String) return Fail($"The value at {tokenPointer} in the sign-in response is not text.");
            var token = tokenElement.GetString()!.Trim();
            if (token.Length == 0) return Fail($"The value at {tokenPointer} in the sign-in response is empty.");
            if (token.Length > MaxTokenLength) return Fail($"The token at {tokenPointer} is too long.");
            if (token.Any(c => c is <= ' ' or > '~')) return Fail($"The token at {tokenPointer} contains characters that cannot be sent in a header.");

            JsonElement? lifetime = null;
            if (!string.IsNullOrWhiteSpace(expiryPointer) && JsonPointer.TryResolve(root, expiryPointer, out var l)) lifetime = l.Clone();
            return new TokenResponse(token, TokenLifetime.Resolve(lifetime, fixedSeconds), null);
        }
    }

    private static TokenResponse Fail(string message) => new(null, 0, message);
}

/// <summary>Secret handling that is pure: the hint shown beside a stored secret, and what a secret may contain.</summary>
public static class SecretRules
{
    public const int MaxLength = 4096;
    /// <summary>A hint is the last four characters, and only for a secret long enough that four characters give little away.</summary>
    public const int MinLengthForHint = 8;
    public const int HintLength = 4;

    public static string? Hint(string? secret) => secret is { } s && s.Length >= MinLengthForHint ? s[^HintLength..] : null;

    /// <summary>Null when the (already trimmed) secret may be stored.</summary>
    public static string? Check(string secret)
    {
        if (secret.Length == 0) return "A value is required.";
        if (secret.Length > MaxLength) return $"A secret is at most {MaxLength} characters.";
        if (secret.Any(char.IsControl)) return "A secret cannot contain line breaks or control characters.";
        return null;
    }
}

/// <summary>
/// Makes text safe to store and show as an error: known secrets (and the forms they take in a URL, JSON or a header) are replaced, control characters removed
/// and the length capped. Connector code never puts a response body or a request into a message; this is the second line of defence for everything else
/// (exception messages, server-supplied status text).
/// </summary>
public static class ErrorSanitizer
{
    public const int MaxLength = 300;
    private const int MinSecretLength = 3;

    public static string Clean(string? text, IEnumerable<string?> secrets, int maxLength = MaxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = text;
        foreach (var secret in secrets.Where(s => s is { Length: >= MinSecretLength }).Select(s => s!).Distinct().OrderByDescending(s => s.Length))
            foreach (var form in Forms(secret))
                result = result.Replace(form, "***", StringComparison.Ordinal);
        var sb = new StringBuilder(result.Length);
        foreach (var c in result) sb.Append(char.IsControl(c) ? ' ' : c);
        result = sb.ToString().Trim();
        return result.Length <= maxLength ? result : result[..maxLength].TrimEnd() + "...";
    }

    private static IEnumerable<string> Forms(string secret)
    {
        yield return secret;
        yield return Uri.EscapeDataString(secret);
        yield return System.Text.Json.JsonEncodedText.Encode(secret).ToString();
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    }
}
