using System.Text;
using NetVips;

namespace Dam.UnitTests.Support;

/// <summary>Builds small real image, PDF and Office files in memory, so ingest code is tested against genuine formats.</summary>
public static class TestImages
{
    /// <summary>A greyscale picture: a bright disc on a dark background.</summary>
    public static byte[] Disc(int size = 256, double radiusFraction = 0.35, string format = ".jpg")
    {
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = x - size / 2.0; var dy = y - size / 2.0;
                pixels[y * size + x] = Math.Sqrt(dx * dx + dy * dy) < size * radiusFraction ? (byte)230 : (byte)25;
            }
        return Encode(pixels, size, size, 1, format);
    }

    /// <summary>A landscape or portrait picture of an exact size: the disc, stretched with edge pixels.</summary>
    public static byte[] Sized(int width, int height, string format = ".jpg", double radiusFraction = 0.35)
    {
        using var disc = Image.NewFromBuffer(Disc(Math.Min(width, height), radiusFraction));
        using var grown = disc.Embed((width - disc.Width) / 2, (height - disc.Height) / 2, width, height, extend: Enums.Extend.Copy);
        return grown.WriteToBuffer(format);
    }

    /// <summary>A very different picture: coarse black and white checks.</summary>
    public static byte[] Checkerboard(int size = 256, string format = ".jpg")
    {
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++) pixels[y * size + x] = ((x / 32 + y / 32) % 2) == 0 ? (byte)255 : (byte)0;
        return Encode(pixels, size, size, 1, format);
    }

    /// <summary>Random noise: incompressible, so the file is as large as its pixel data.</summary>
    public static byte[] Noise(int width, int height, int seed, string format = ".png")
    {
        var pixels = new byte[width * height * 3];
        new Random(seed).NextBytes(pixels);
        return Encode(pixels, width, height, 3, format);
    }

    public static byte[] Resize(byte[] image, int width, string format = ".jpg")
    {
        using var src = Image.NewFromBuffer(image);
        using var small = src.ThumbnailImage(width);
        return small.WriteToBuffer(format);
    }

    private static byte[] Encode(byte[] pixels, int w, int h, int bands, string format)
    {
        using var img = Image.NewFromMemory(pixels, w, h, bands, Enums.BandFormat.Uchar);
        return img.WriteToBuffer(format);
    }

    /// <summary>Inserts an EXIF block (Make and Model) right after the JPEG start marker.</summary>
    public static byte[] WithExif(byte[] jpeg, string make, string model)
    {
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");
        var (mk, md) = (Ascii(make), Ascii(model));
        const int ifdOffset = 8, entries = 2, ifdSize = 2 + entries * 12 + 4;
        var dataStart = ifdOffset + ifdSize;
        var tiff = new List<byte> { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, entries, 0 };
        void Entry(ushort tag, byte[] data, int offset)
        {
            tiff.AddRange(BitConverter.GetBytes(tag)); tiff.AddRange(BitConverter.GetBytes((ushort)2));
            tiff.AddRange(BitConverter.GetBytes(data.Length)); tiff.AddRange(BitConverter.GetBytes(offset));
        }
        Entry(0x010F, mk, dataStart);
        Entry(0x0110, md, dataStart + mk.Length);
        tiff.AddRange(new byte[4]);       // no next IFD
        tiff.AddRange(mk); tiff.AddRange(md);
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var length = 2 + 6 + tiff.Count;
        app1.Add((byte)(length >> 8)); app1.Add((byte)length);
        app1.AddRange("Exif\0\0"u8.ToArray()); app1.AddRange(tiff);
        return jpeg[..2].Concat(app1).Concat(jpeg[2..]).ToArray();
    }

    /// <summary>A one-page PDF with a title, with a correct cross-reference table.</summary>
    public static byte[] Pdf(string title)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            $"<< /Title ({title}) /Author (Acme Docs) /Producer (Orbit tests) >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A minimal .docx: a zip with document properties.</summary>
    public static byte[] Docx(string title, string author)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string body) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(body); }
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\">" +
                $"<dc:title>{title}</dc:title><dc:creator>{author}</dc:creator><dcterms:created>2026-01-02T03:04:05Z</dcterms:created></cp:coreProperties>");
            Add("docProps/app.xml", "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Acme Writer</Application><Pages>3</Pages></Properties>");
        }
        return ms.ToArray();
    }

    /// <summary>The standard EICAR antivirus test string, assembled at run time so this source file is not itself flagged by antivirus.</summary>
    public static byte[] Eicar() =>
        Encoding.ASCII.GetBytes(string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-TEST-FILE!", "$H+H*"));
}
