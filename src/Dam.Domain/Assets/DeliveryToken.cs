using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dam.Domain.Assets;

/// <summary>What a delivery link allows. It is signed, so a client cannot widen it.</summary>
/// <param name="Scope">"r:thumb,web" (named renditions) or "t:w=800&amp;fit=cover…" (exactly one transform).</param>
/// <param name="Ip">When set, only this client address may use the link.</param>
/// <param name="Watermark">Set when the asset is not published or its rights require it, so the link never yields a clean copy of unreleased or restricted content.</param>
/// <param name="Channel">The consumer's channel the link was issued for (ADR-0033), if it said. Rights are re-checked against it when the link is used, and a delivery log can attribute the fetch.</param>
/// <param name="Region">The consumer's region the link was issued for, if it said.</param>
public sealed record DeliveryClaims(Guid TenantId, Guid AssetId, string Scope, DateTimeOffset ExpiresAt, string? Ip, bool Watermark, string? Channel = null, string? Region = null)
{
    public bool AllowsRendition(string profile) =>
        Scope.StartsWith("r:", StringComparison.Ordinal) && Scope[2..].Split(',').Contains(profile);

    public string? TransformScope => Scope.StartsWith("t:", StringComparison.Ordinal) ? Scope[2..] : null;
}

public enum TokenFailure { None, Malformed, BadSignature, Expired }

/// <summary>
/// HMAC-SHA256 signed, self-contained delivery tokens (spec A7.6). The first key signs; every listed key verifies, so the signing
/// key can be rotated without breaking links that are already out.
/// </summary>
public sealed class DeliveryTokenSigner
{
    private readonly byte[][] _keys;

    public DeliveryTokenSigner(IEnumerable<string> keys)
    {
        _keys = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => Encoding.UTF8.GetBytes(k.Trim())).ToArray();
        if (_keys.Length == 0) throw new ArgumentException("At least one signing key is required.", nameof(keys));
        if (_keys.Any(k => k.Length < 32)) throw new ArgumentException("Signing keys must be at least 32 characters.", nameof(keys));
    }

    public string Sign(DeliveryClaims c)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(c.TenantId.ToString("N"), c.AssetId.ToString("N"), c.Scope, c.ExpiresAt.ToUnixTimeSeconds(), c.Ip, c.Watermark ? 1 : 0, c.Channel, c.Region));
        var body = Base64Url(payload);
        return body + "." + Base64Url(HMACSHA256.HashData(_keys[0], Encoding.ASCII.GetBytes(body)));
    }

    public (DeliveryClaims? Claims, TokenFailure Failure) Verify(string token, DateTimeOffset now)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1 || token.Length > 2048) return (null, TokenFailure.Malformed);
        var body = token[..dot];
        byte[] given, payload;
        try { given = FromBase64Url(token[(dot + 1)..]); payload = FromBase64Url(body); }
        catch (FormatException) { return (null, TokenFailure.Malformed); }

        var bodyBytes = Encoding.ASCII.GetBytes(body);
        var valid = false;
        foreach (var key in _keys) valid |= CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, bodyBytes), given);
        if (!valid) return (null, TokenFailure.BadSignature);

        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(payload); }
        catch (JsonException) { return (null, TokenFailure.Malformed); }
        if (p is null || !Guid.TryParseExact(p.T, "N", out var tenant) || !Guid.TryParseExact(p.A, "N", out var asset)) return (null, TokenFailure.Malformed);

        var claims = new DeliveryClaims(tenant, asset, p.S, DateTimeOffset.FromUnixTimeSeconds(p.E), p.Ip, p.W == 1, p.C, p.R);
        return claims.ExpiresAt <= now ? (null, TokenFailure.Expired) : (claims, TokenFailure.None);
    }

    /// <summary>C and R are optional and left out when unset, so tokens signed before they existed still verify and unchanged tokens are byte-identical.</summary>
    private sealed record Payload(string T, string A, string S, long E, string? Ip, int W,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? C = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? R = null);

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
