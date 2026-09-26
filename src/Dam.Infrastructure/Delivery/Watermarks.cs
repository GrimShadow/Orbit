using NetVips;

namespace Dam.Infrastructure.Delivery;

/// <summary>
/// Draws a tiled, slanted word across an image (spec A7.15). The letters come from a built-in 5x7 bitmap font, so watermarking
/// needs no system fonts and looks the same in every container.
/// </summary>
public static class Watermarks
{
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['B'] = ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
        ['C'] = ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['F'] = ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
        ['G'] = ["01110", "10001", "10000", "10111", "10001", "10001", "01111"],
        ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['I'] = ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
        ['J'] = ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
        ['K'] = ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['M'] = ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
        ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ['Q'] = ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['S'] = ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
        ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
        ['U'] = ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
        ['W'] = ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
        ['X'] = ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
        ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
        ['Z'] = ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
    };

    /// <summary>Returns a new image with the word tiled over it. The input is not disposed.</summary>
    public static Image Apply(Image image, string text)
    {
        var word = new string(text.ToUpperInvariant().Where(c => Glyphs.ContainsKey(c) || c == ' ').ToArray());
        if (word.Length == 0) return image;

        var scale = Math.Max(2, Math.Min(image.Width, image.Height) / 90);
        var (tileW, tileH, tile) = Tile(word, scale);

        var diagonal = (int)Math.Ceiling(Math.Sqrt((double)image.Width * image.Width + (double)image.Height * image.Height)) + tileW;
        using var raw = Image.NewFromMemory(tile, tileW, tileH, 4, Enums.BandFormat.Uchar);
        using var single = raw.Copy(interpretation: Enums.Interpretation.Srgb);   // raw bytes are "multiband"; say they are colour
        using var grid = single.Replicate(diagonal / tileW + 2, diagonal / tileH + 2);
        using var rotated = grid.Similarity(angle: -30);
        var left = Math.Max(0, (rotated.Width - image.Width) / 2);
        var top = Math.Max(0, (rotated.Height - image.Height) / 2);
        using var overlay = rotated.ExtractArea(left, top, Math.Min(image.Width, rotated.Width - left), Math.Min(image.Height, rotated.Height - top));

        // The overlay is RGBA, so the picture must be RGB(A) too (greyscale sources are widened here only).
        using var rgb = image.Interpretation == Enums.Interpretation.Srgb ? image.Copy() : image.Colourspace(Enums.Interpretation.Srgb);
        using var withAlpha = rgb.HasAlpha() ? rgb.Copy() : rgb.Bandjoin(255);
        using var tagged = withAlpha.Copy(interpretation: Enums.Interpretation.Srgb);
        return tagged.Composite2(overlay, Enums.BlendMode.Over);
    }

    private static (int W, int H, byte[] Rgba) Tile(string word, int scale)
    {
        const int glyphW = 5, glyphH = 7, spacing = 1;
        var textW = word.Length * (glyphW + spacing) - spacing;
        var (gapX, gapY) = (glyphW * scale * 3, glyphH * scale * 3);
        var (w, h) = (textW * scale + gapX, glyphH * scale + gapY);
        var rgba = new byte[w * h * 4];

        for (var i = 0; i < word.Length; i++)
        {
            if (!Glyphs.TryGetValue(word[i], out var rows)) continue;
            for (var row = 0; row < glyphH; row++)
                for (var col = 0; col < glyphW; col++)
                {
                    if (rows[row][col] != '1') continue;
                    for (var dy = 0; dy < scale; dy++)
                        for (var dx = 0; dx < scale; dx++)
                        {
                            var x = (i * (glyphW + spacing) + col) * scale + dx;
                            var y = row * scale + dy;
                            var o = (y * w + x) * 4;
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = 128;     // mid grey reads on light and dark pictures alike
                            rgba[o + 3] = 150;
                        }
                }
        }
        return (w, h, rgba);
    }
}
