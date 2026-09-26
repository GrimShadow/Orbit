using Dam.Domain.Assets;
using NetVips;

namespace Dam.Infrastructure.Delivery;

/// <summary>How to render one output image. <see cref="AllowUpscale"/> is false for named profiles, which only ever shrink.</summary>
public sealed record RenderRequest(
    int? Width, int? Height, string Fit, string Format, int Quality, double? FocalX = null, double? FocalY = null,
    bool Watermark = false, bool AllowUpscale = false)
{
    public static RenderRequest ForProfile(RenditionProfile p, bool watermark = false) =>
        new(p.LongestEdge, p.LongestEdge, "contain", p.Format, p.Quality, Watermark: watermark);

    public static RenderRequest ForTransform(TransformSpec t, bool watermark = false) =>
        new(t.Width, t.Height, t.Fit, t.Format, t.Quality, t.FocalX, t.FocalY, watermark, AllowUpscale: t.Fit != "contain");
}

public sealed record RenderedImage(byte[] Bytes, string Mime, int Width, int Height);

public sealed class UnsupportedImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>libvips (through NetVips) resizing, cropping, format conversion and watermarking (spec A6). Metadata is always stripped.</summary>
public static class ImageRenderer
{
    public static RenderedImage Render(byte[] source, RenderRequest r)
    {
        try
        {
            using var header = Image.NewFromBuffer(source);
            var (srcW, srcH) = OrientedSize(header);

            var (boxW, boxH) = (r.Width, r.Height) switch
            {
                (int w, int h) => (w, h),
                (int w, null) => (w, (int)Math.Max(1, Math.Round((double)srcH * w / srcW))),
                (null, int h) => ((int)Math.Max(1, Math.Round((double)srcW * h / srcH)), h),
                _ => throw new ArgumentException("A width or a height is required."),
            };

            Image img;
            if (r.Fit == "fill" && r.Width is not null && r.Height is not null)
            {
                var (w, h) = Clamp(boxW, boxH, srcW, srcH, r.AllowUpscale, exact: true);
                img = Image.ThumbnailBuffer(source, w, height: h, size: Enums.Size.Force);
            }
            else if (r.Fit == "cover" && r.Width is not null && r.Height is not null)
            {
                var scale = Math.Max((double)boxW / srcW, (double)boxH / srcH);
                if (!r.AllowUpscale) scale = Math.Min(scale, 1);
                var (scaledW, scaledH) = ((int)Math.Max(boxW <= srcW || r.AllowUpscale ? boxW : 1, Math.Ceiling(srcW * scale)), (int)Math.Max(boxH <= srcH || r.AllowUpscale ? boxH : 1, Math.Ceiling(srcH * scale)));
                using var scaled = Image.ThumbnailBuffer(source, scaledW, height: scaledH, size: Enums.Size.Force);
                var (cropW, cropH) = (Math.Min(boxW, scaled.Width), Math.Min(boxH, scaled.Height));
                var left = Math.Clamp((int)Math.Round((r.FocalX ?? 0.5) * scaled.Width - cropW / 2.0), 0, scaled.Width - cropW);
                var top = Math.Clamp((int)Math.Round((r.FocalY ?? 0.5) * scaled.Height - cropH / 2.0), 0, scaled.Height - cropH);
                img = scaled.ExtractArea(left, top, cropW, cropH);
            }
            else
            {
                // contain (and a single dimension): fit inside the box, keeping the picture's shape
                var scale = Math.Min((double)boxW / srcW, (double)boxH / srcH);
                if (!r.AllowUpscale) scale = Math.Min(scale, 1);
                var (w, h) = ((int)Math.Max(1, Math.Round(srcW * scale)), (int)Math.Max(1, Math.Round(srcH * scale)));
                img = Image.ThumbnailBuffer(source, w, height: h, size: Enums.Size.Force);
            }

            using (img)
            {
                var prepared = Prepare(img, r.Format);
                using var final = r.Watermark ? Watermarks.Apply(prepared, "PREVIEW") : prepared;
                var bytes = final.WriteToBuffer(Suffix(r.Format, r.Quality));
                return new RenderedImage(bytes, RenditionProfiles.MimeOf(r.Format), final.Width, final.Height);
            }
        }
        catch (VipsException ex)
        {
            throw new UnsupportedImageException("The image could not be read or converted.", ex);
        }
    }

    private static (int W, int H) OrientedSize(Image image)
    {
        var swap = image.GetTypeOf("orientation") != 0 && image.Get("orientation") is int o && o is >= 5 and <= 8;
        return swap ? (image.Height, image.Width) : (image.Width, image.Height);
    }

    private static (int W, int H) Clamp(int w, int h, int srcW, int srcH, bool allowUpscale, bool exact) =>
        allowUpscale || !exact ? (w, h) : (Math.Min(w, srcW), Math.Min(h, srcH));

    /// <summary>sRGB, and flattened onto white for formats that have no transparency.</summary>
    private static Image Prepare(Image img, string format)
    {
        var result = img.Interpretation is Enums.Interpretation.Srgb or Enums.Interpretation.Bw ? img : img.Colourspace(Enums.Interpretation.Srgb);
        if (format == "jpg" && result.HasAlpha()) result = result.Flatten(background: new double[] { 255, 255, 255 });
        return result;
    }

    private static string Suffix(string format, int quality) => format switch
    {
        "webp" => $".webp[Q={quality},strip]",
        "avif" => $".avif[Q={quality},strip]",
        "png" => ".png[strip]",
        _ => $".jpg[Q={quality},strip,optimize_coding]",
    };
}
