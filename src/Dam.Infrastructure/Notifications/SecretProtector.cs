using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;

namespace Dam.Infrastructure.Notifications;

/// <summary>
/// AES-256-GCM under a key derived (HKDF-SHA256) from <c>DAM_SECRETS_KEY</c>. A stored value is <c>v1.</c> and base64 of nonce, tag and
/// ciphertext; the purpose is authenticated as associated data, so a ciphertext cannot be swapped between uses. Without a key every call
/// fails with an explanatory error rather than storing secrets in the clear.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "v1.";
    private const int NonceSize = 12, TagSize = 16;
    private readonly byte[]? _key;

    public AesGcmSecretProtector(string? secretsKey)
    {
        if (string.IsNullOrWhiteSpace(secretsKey)) return;
        if (secretsKey.Length < 16) throw new InvalidOperationException("DAM_SECRETS_KEY must be at least 16 characters (use 32 random bytes, base64).");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secretsKey), 32, salt: null, info: Encoding.UTF8.GetBytes("orbit/secrets/v1"));
    }

    public string Protect(string plaintext, string purpose)
    {
        var key = Key();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        var key = Key();
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unknown secret format.");
        var raw = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (raw.Length < NonceSize + TagSize) throw new CryptographicException("The stored secret is truncated.");
        var plain = new byte[raw.Length - NonceSize - TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(raw.AsSpan(0, NonceSize), raw.AsSpan(NonceSize + TagSize), raw.AsSpan(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(purpose));
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] Key() => _key ?? throw new InvalidOperationException("DAM_SECRETS_KEY is not set, so secrets cannot be stored or read. Set it to 32 random bytes, base64.");
}
