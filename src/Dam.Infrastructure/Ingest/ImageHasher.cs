using Dam.Domain.Assets;

namespace Dam.Infrastructure.Ingest;

/// <summary>Decodes an image with libvips, shrinks it to 32x32 greyscale and returns its perceptual hash.</summary>
public static class ImageHasher
{
    /// <returns>Null when the bytes are not an image libvips can decode (damaged, or a format without a decoder).</returns>
    public static ulong? HashOf(Stream image)
    {
        try
        {
            using var thumb = NetVips.Image.ThumbnailStream(image, PerceptualHash.Side, height: PerceptualHash.Side, size: NetVips.Enums.Size.Force);
            using var flat = thumb.HasAlpha() ? thumb.Flatten(background: [255.0]) : thumb.Copy();   // transparent areas count as white
            using var grey = flat.Colourspace(NetVips.Enums.Interpretation.Bw);
            using var one = grey.Bands > 1 ? grey.ExtractBand(0) : grey.Copy();
            using var bytes = one.Cast(NetVips.Enums.BandFormat.Uchar);
            var pixels = bytes.WriteToMemory<byte>();
            return pixels.Length == PerceptualHash.Side * PerceptualHash.Side ? PerceptualHash.FromGrayscale(pixels) : null;
        }
        catch (NetVips.VipsException) { return null; }
    }
}
