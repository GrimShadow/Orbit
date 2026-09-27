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
    public static byte[] Pdf(string title) => Pdf(title, pages: 1);

    /// <summary>A PDF with a title and <paramref name="pages"/> blank pages, with a correct cross-reference table.</summary>
    public static byte[] Pdf(string title, int pages)
    {
        var objects = new List<string> { $"<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3, pages).Select(n => $"{n} 0 R"))}] /Count {pages} >>" };
        for (var p = 0; p < pages; p++) objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> >>");
        objects.Add($"<< /Title ({title}) /Author (Acme Docs) /Producer (Orbit tests) >>");

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {objects.Count} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A scratch folder marked so desktop search (Spotlight and equivalents) never indexes the short-lived fixture files
    /// this class and the media renderers under test churn through.</summary>
    private static string ScratchDir()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dam-media-scratch");
        Directory.CreateDirectory(dir);
        var marker = System.IO.Path.Combine(dir, ".metadata_never_index");
        if (!File.Exists(marker)) { try { File.WriteAllBytes(marker, []); } catch (IOException) { } }
        return dir;
    }

    /// <summary>A short synthetic video (a colour test pattern), built at test time with the system's own ffmpeg.</summary>
    public static byte[] Video(int seconds = 3, int width = 320, int height = 240)
    {
        var input = System.IO.Path.Combine(ScratchDir(), $"dam-fixture-{Guid.NewGuid():N}.mp4");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-y", "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=15:duration={seconds}", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "ultrafast", input })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { p.Kill(true); throw new TimeoutException("ffmpeg took too long to build the test fixture."); }
            if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg fixture build failed: {err}");
            return File.ReadAllBytes(input);
        }
        finally { try { File.Delete(input); } catch (IOException) { } }
    }

    /// <summary>A minimal .docx: a zip with document properties.</summary>
    /// <summary>A real, minimal .docx: LibreOffice can open and convert it, not just read its properties. The main part lives at the
    /// fixed OOXML path "word/document.xml", which is how the content sniffer tells a Word document from any other zip.</summary>
    public static byte[] Docx(string title, string author) => Zip(new()
    {
        ["[Content_Types].xml"] = Types(
            ("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
        ["_rels/.rels"] = Rels("word/document.xml"),
        ["word/document.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Service plan for Acme test fixtures.</w:t></w:r></w:p></w:body></w:document>""",
        ["docProps/core.xml"] = CoreProps(title, author),
        ["docProps/app.xml"] = AppProps("Acme Writer", ("Pages", "3")),
    });

    /// <summary>A real, minimal .pptx: one slide with a title, openable by LibreOffice. Main part at "ppt/presentation.xml".</summary>
    public static byte[] Pptx(string title, string author) => Zip(new()
    {
        ["[Content_Types].xml"] = Types(
            ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
            ("/ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"),
            ("/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"),
            ("/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml")),
        ["_rels/.rels"] = Rels("ppt/presentation.xml"),
        ["ppt/presentation.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdM1"/></p:sldMasterIdLst><p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst><p:sldSz cx="9144000" cy="6858000"/></p:presentation>""",
        ["ppt/_rels/presentation.xml.rels"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/><Relationship Id="rIdM1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/></Relationships>""",
        ["ppt/slides/slide1.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/><p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:t>Acme campaign overview</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>""",
        ["ppt/slides/_rels/slide1.xml.rels"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/></Relationships>""",
        ["ppt/slideLayouts/slideLayout1.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank" preserve="1"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld></p:sldLayout>""",
        ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>""",
        ["ppt/slideMasters/slideMaster1.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"/></p:sldLayoutIdLst></p:sldMaster>""",
        ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/></Relationships>""",
        ["docProps/core.xml"] = CoreProps(title, author),
        ["docProps/app.xml"] = AppProps("Acme Slides", ("Slides", "1")),
    });

    /// <summary>A real, minimal .xlsx: one sheet with one cell, openable by LibreOffice. Main part at "xl/workbook.xml".</summary>
    public static byte[] Xlsx(string title, string author) => Zip(new()
    {
        ["[Content_Types].xml"] = Types(
            ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
            ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
        ["_rels/.rels"] = Rels("xl/workbook.xml"),
        ["xl/workbook.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""",
        ["xl/_rels/workbook.xml.rels"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""",
        ["xl/worksheets/sheet1.xml"] =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Acme quarterly totals</t></is></c></row></sheetData></worksheet>""",
        ["docProps/core.xml"] = CoreProps(title, author),
        ["docProps/app.xml"] = AppProps("Acme Sheets"),
    });

    private static string Types(params (string PartName, string ContentType)[] overrides) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""" +
        """<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>""" +
        string.Concat(overrides.Select(o => $"""<Override PartName="{o.PartName}" ContentType="{o.ContentType}"/>""")) + "</Types>";

    /// <summary>The package's top-level relationships: the main part, plus the core/app properties every real Office file carries.</summary>
    private static string Rels(string mainTarget) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
        $"""<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{mainTarget}"/>""" +
        """<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>""" +
        """<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/></Relationships>""";

    private static string CoreProps(string title, string author) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">""" +
        $"""<dc:title>{title}</dc:title><dc:creator>{author}</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">2026-01-02T03:04:05Z</dcterms:created></cp:coreProperties>""";

    private static string AppProps(string application, params (string Name, string Value)[] extra) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">""" +
        $"<Application>{application}</Application>" + string.Concat(extra.Select(e => $"<{e.Name}>{e.Value}</{e.Name}>")) + "</Properties>";

    private static byte[] Zip(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(body);
            }
        return ms.ToArray();
    }

    /// <summary>The standard EICAR antivirus test string, assembled at run time so this source file is not itself flagged by antivirus.</summary>
    public static byte[] Eicar() =>
        Encoding.ASCII.GetBytes(string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-TEST-FILE!", "$H+H*"));
}
