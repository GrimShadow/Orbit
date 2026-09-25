using System.Security.Cryptography;

namespace Dam.Domain.Common;

/// <summary>RFC 9562 UUID v7 (time-ordered) generator (decision D14).</summary>
public static class Uuid7
{
    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        var ms = timestamp.ToUnixTimeMilliseconds();
        b[0] = (byte)(ms >> 40); b[1] = (byte)(ms >> 32); b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16); b[4] = (byte)(ms >> 8); b[5] = (byte)ms;
        b[6] = (byte)((b[6] & 0x0F) | 0x70);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }
}
