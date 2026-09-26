using System.Numerics;

namespace Dam.Domain.Assets;

/// <summary>
/// 64-bit perceptual hash (pHash) of an image: similar-looking pictures get hashes that differ in only a few bits, even after
/// resizing, recompression or small colour changes. The input is the image scaled to 32x32 greyscale; decoding and scaling
/// happen in the worker (libvips), so this is only the maths and is fully unit-testable.
/// </summary>
public static class PerceptualHash
{
    public const int Side = 32;
    private const int Low = 8; // keep the 8x8 lowest frequencies

    private static readonly double[,] Cos = BuildCosines();

    private static double[,] BuildCosines()
    {
        var c = new double[Low, Side];
        for (var u = 0; u < Low; u++)
            for (var x = 0; x < Side; x++)
                c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
        return c;
    }

    /// <param name="grey">32x32 greyscale pixels, row by row (1024 bytes).</param>
    public static ulong FromGrayscale(ReadOnlySpan<byte> grey)
    {
        if (grey.Length != Side * Side) throw new ArgumentException($"Expected {Side * Side} pixels.", nameof(grey));

        // 2D DCT, keeping only the low frequencies: first along rows, then down columns.
        var rows = new double[Side, Low];
        for (var y = 0; y < Side; y++)
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var x = 0; x < Side; x++) sum += grey[y * Side + x] * Cos[u, x];
                rows[y, u] = sum;
            }
        var dct = new double[Low * Low];
        for (var v = 0; v < Low; v++)
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var y = 0; y < Side; y++) sum += rows[y, u] * Cos[v, y];
                dct[v * Low + u] = sum;
            }

        // Each bit says whether a frequency is above the median. The DC term (overall brightness) is left out of the median.
        var sorted = dct.Skip(1).Order().ToArray();
        var median = sorted[sorted.Length / 2];
        ulong hash = 0;
        for (var i = 0; i < dct.Length; i++) if (dct[i] > median) hash |= 1UL << i;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static string ToHex(ulong hash) => hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? hex, out ulong hash)
    {
        hash = 0;
        return hex is { Length: 16 } && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash);
    }
}
