using System.Text;
using Dam.Domain.Assets;
using Dam.Domain.Content;

namespace Dam.UnitTests;

public sealed class ContentSnifferTests
{
    private static byte[] B(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();
    private static byte[] Pad(byte[] head, int total = 64) => head.Concat(new byte[Math.Max(0, total - head.Length)]).ToArray();
    private static byte[] Ftyp(string brand) => Pad(B(0, 0, 0, 0x18).Concat("ftyp"u8.ToArray()).Concat(Encoding.ASCII.GetBytes(brand)).ToArray());
    private static byte[] Riff(string kind) => Pad("RIFF"u8.ToArray().Concat(B(0x24, 0, 0, 0)).Concat(Encoding.ASCII.GetBytes(kind)).ToArray());

    public static IEnumerable<object[]> Formats() =>
    [
        [Pad(B(0xFF, 0xD8, 0xFF, 0xE0)), ContentKind.Image, "image/jpeg"],
        [Pad(B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)), ContentKind.Image, "image/png"],
        [Pad("GIF89a"u8.ToArray()), ContentKind.Image, "image/gif"],
        [Pad("GIF87a"u8.ToArray()), ContentKind.Image, "image/gif"],
        [Riff("WEBP"), ContentKind.Image, "image/webp"],
        [Pad(B(0x49, 0x49, 0x2A, 0x00)), ContentKind.Image, "image/tiff"],
        [Pad(B(0x4D, 0x4D, 0x00, 0x2A)), ContentKind.Image, "image/tiff"],
        [Pad("8BPS"u8.ToArray()), ContentKind.Image, "image/vnd.adobe.photoshop"],
        [Ftyp("heic"), ContentKind.Image, "image/heic"],
        [Ftyp("avif"), ContentKind.Image, "image/avif"],
        [Ftyp("isom"), ContentKind.Video, "video/mp4"],
        [Ftyp("mp42"), ContentKind.Video, "video/mp4"],
        [Ftyp("qt  "), ContentKind.Video, "video/quicktime"],
        [Ftyp("M4A "), ContentKind.Audio, "audio/mp4"],
        [Pad(B(0, 0, 0, 8).Concat("moov"u8.ToArray()).ToArray()), ContentKind.Video, "video/quicktime"],
        [Pad(B(0x1A, 0x45, 0xDF, 0xA3)), ContentKind.Video, "video/x-matroska"],
        [Riff("AVI "), ContentKind.Video, "video/x-msvideo"],
        [Pad(B(0x06, 0x0E, 0x2B, 0x34)), ContentKind.Video, "application/mxf"],
        [Riff("WAVE"), ContentKind.Audio, "audio/wav"],
        [Pad("ID3"u8.ToArray()), ContentKind.Audio, "audio/mpeg"],
        [Pad(B(0xFF, 0xFB, 0x90, 0x00)), ContentKind.Audio, "audio/mpeg"],
        [Pad(B(0xFF, 0xF1, 0x50, 0x80)), ContentKind.Audio, "audio/aac"],
        [Pad("fLaC"u8.ToArray()), ContentKind.Audio, "audio/flac"],
        [Pad("OggS"u8.ToArray()), ContentKind.Audio, "audio/ogg"],
        [Pad("glTF"u8.ToArray()), ContentKind.Model3d, "model/gltf-binary"],
        [Pad("%PDF-1.7\n"u8.ToArray()), ContentKind.Pdf, "application/pdf"],
        [Pad(B(0x50, 0x4B, 0x03, 0x04)), ContentKind.Archive, "application/zip"],
        [Pad(B(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)), ContentKind.Office, "application/x-ole-storage"],
        ["<svg xmlns='http://www.w3.org/2000/svg'></svg>"u8.ToArray(), ContentKind.Svg, "image/svg+xml"],
        ["  <?xml version='1.0'?>\n<svg></svg>"u8.ToArray(), ContentKind.Svg, "image/svg+xml"],
        ["<!DOCTYPE html><html></html>"u8.ToArray(), ContentKind.Html, "text/html"],
        ["<html><body>x</body></html>"u8.ToArray(), ContentKind.Html, "text/html"],
        ["{\\rtf1\\ansi hello}"u8.ToArray(), ContentKind.Text, "application/rtf"],
        ["name,region\nAcme,North\n"u8.ToArray(), ContentKind.Text, "text/plain"],
        [B(0xEF, 0xBB, 0xBF).Concat("hello, world"u8.ToArray()).ToArray(), ContentKind.Text, "text/plain"],
    ];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Formats_are_recognised_from_their_bytes(byte[] head, ContentKind kind, string mime)
    {
        var r = ContentSniffer.Sniff(head);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(mime, r.Mime);
    }

    [Fact]
    public void A_pdf_marker_is_found_within_the_first_kilobyte()
    {
        var withJunk = Encoding.ASCII.GetBytes(new string(' ', 200) + "%PDF-1.4").Concat(new byte[100]).ToArray();
        Assert.Equal(ContentKind.Pdf, ContentSniffer.Sniff(withJunk).Kind);
        var tooLate = Encoding.ASCII.GetBytes(new string(' ', 2000) + "%PDF-1.4");
        Assert.NotEqual(ContentKind.Pdf, ContentSniffer.Sniff(tooLate).Kind);
    }

    [Theory]
    [InlineData("word/document.xml", OfficeMime.Docx, "docx")]
    [InlineData("ppt/presentation.xml", OfficeMime.Pptx, "pptx")]
    [InlineData("xl/workbook.xml", OfficeMime.Xlsx, "xlsx")]
    public void A_real_office_documents_zip_entries_reveal_which_kind_it_is_not_just_that_it_is_a_zip(string mainPart, string mime, string format)
    {
        var withEntry = B(0x50, 0x4B, 0x03, 0x04).Concat(new byte[20]).Concat(Encoding.ASCII.GetBytes(mainPart)).Concat(new byte[40]).ToArray();
        var r = ContentSniffer.Sniff(withEntry);
        Assert.Equal(ContentKind.Office, r.Kind);
        Assert.Equal(mime, r.Mime);
        Assert.Equal(format, r.Format);
        Assert.Equal([format], r.Extensions);
    }

    [Fact]
    public void A_zip_with_no_office_marker_is_just_a_generic_zip()
    {
        var r = ContentSniffer.Sniff(Pad(B(0x50, 0x4B, 0x03, 0x04)));
        Assert.Equal(ContentKind.Archive, r.Kind);
        Assert.Equal("application/zip", r.Mime);
        Assert.Contains("docx", r.Extensions);   // still allowed to hold one, just not detected as one from its bytes
    }

    public static IEnumerable<object[]> Executables() =>
    [
        [Pad(B(0x4D, 0x5A, 0x90, 0x00)), "pe"],
        [Pad(B(0x7F, 0x45, 0x4C, 0x46, 2, 1, 1)), "elf"],
        [Pad(B(0xFE, 0xED, 0xFA, 0xCF)), "macho"],
        [Pad(B(0xCF, 0xFA, 0xED, 0xFE)), "macho"],
        [Pad(B(0xCA, 0xFE, 0xBA, 0xBE)), "macho"],
        ["#!/bin/sh\nrm -rf /\n"u8.ToArray(), "script"],
    ];

    [Theory]
    [MemberData(nameof(Executables))]
    public void Executables_are_detected_and_always_refused_whatever_the_asset_type_or_file_name(byte[] head, string format)
    {
        var sniff = ContentSniffer.Sniff(head);
        Assert.Equal(ContentKind.Executable, sniff.Kind);
        Assert.Equal(format, sniff.Format);
        foreach (var type in AssetTypes.All)
            foreach (var name in new[] { "photo.jpg", "doc.pdf", "clip.mp4", "anything.bin" })
            {
                var v = ContentSniffer.Judge(type, name, sniff);
                Assert.False(v.Allowed, $"{type}/{name}");
                Assert.Contains("executable", v.Reason);
            }
    }

    [Fact]
    public void Binary_data_that_is_not_recognised_is_unknown_and_text_with_nul_bytes_is_not_text()
    {
        Assert.Equal(ContentKind.Unknown, ContentSniffer.Sniff(B(0x01, 0x02, 0x03, 0x04, 0x05)).Kind);
        Assert.Equal(ContentKind.Unknown, ContentSniffer.Sniff(Encoding.ASCII.GetBytes("hello").Concat(B(0)).Concat("world"u8.ToArray()).ToArray()).Kind);
        Assert.Equal(ContentKind.Unknown, ContentSniffer.Sniff(ReadOnlySpan<byte>.Empty).Kind);
        Assert.Equal(ContentKind.Unknown, ContentSniffer.Sniff(B(0xC3, 0x28, 0xA0, 0xA1, 0x41)).Kind); // invalid UTF-8
    }

    // ---- judging content against the asset type ---------------------------------------------------------------

    private static SniffResult Of(byte[] head) => ContentSniffer.Sniff(head);
    private static readonly SniffResult Jpeg = Of(Pad(B(0xFF, 0xD8, 0xFF)));
    private static readonly SniffResult Png = Of(Pad(B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
    private static readonly SniffResult Pdf = Of(Pad("%PDF-1.7"u8.ToArray()));
    private static readonly SniffResult Mp4 = Of(Ftyp("isom"));
    private static readonly SniffResult Mp3 = Of(Pad("ID3"u8.ToArray()));
    private static readonly SniffResult Zip = Of(Pad(B(0x50, 0x4B, 0x03, 0x04)));
    private static readonly SniffResult Glb = Of(Pad("glTF"u8.ToArray()));
    private static readonly SniffResult Csv = Of("a,b\n1,2\n"u8.ToArray());
    private static readonly SniffResult Svg = Of("<svg></svg>"u8.ToArray());

    public static IEnumerable<object[]> Judgements() =>
    [
        // a real match is accepted
        ["image", "a.jpg", Jpeg, true], ["image", "a.png", Png, true], ["image", "logo.svg", Svg, true],
        ["video", "a.mp4", Mp4, true], ["audio", "a.mp3", Mp3, true],
        ["document", "a.pdf", Pdf, true], ["document", "a.docx", Zip, true], ["document", "a.csv", Csv, true],
        ["manual", "a.pdf", Pdf, true], ["manual", "a.epub", Zip, true],
        ["3d", "a.glb", Glb, true], ["3d", "a.zip", Zip, true], ["html5", "banner.zip", Zip, true],
        ["other", "a.xyz", Of(B(1, 2, 3, 4, 5)), true], ["other", "a.jpg", Jpeg, true],
        // the content decides, not the name
        ["image", "a.jpg", Pdf, false], ["image", "a.jpg", Mp4, false], ["image", "a.jpg", Csv, false], ["image", "a.jpg", Zip, false],
        ["video", "a.mp4", Jpeg, false], ["audio", "a.mp3", Mp4, false], ["document", "a.pdf", Jpeg, false],
        ["document", "a.zip", Zip, false],        // a bare zip is not a document (only docx/xlsx/pptx/odf)
        ["html5", "a.docx", Zip, false], ["html5", "a.zip", Pdf, false],
        ["manual", "a.pdf", Mp4, false], ["3d", "a.glb", Pdf, false],
        ["blueprint", "a.pdf", Pdf, false],       // not an asset type at all
    ];

    [Theory]
    [MemberData(nameof(Judgements))]
    public void Content_must_suit_the_asset_type(string type, string name, SniffResult sniff, bool allowed)
    {
        var v = ContentSniffer.Judge(type, name, sniff);
        Assert.Equal(allowed, v.Allowed);
        Assert.Equal(allowed, v.Reason is null);
    }

    [Fact]
    public void A_wrong_file_extension_on_matching_content_is_accepted_with_a_warning()
    {
        var v = ContentSniffer.Judge("image", "holiday.png", Jpeg); // a JPEG saved with a .png name: common, harmless
        Assert.True(v.Allowed);
        Assert.Contains(".png", Assert.Single(v.Warnings));
        Assert.Empty(ContentSniffer.Judge("image", "holiday.jpg", Jpeg).Warnings);
        Assert.Empty(ContentSniffer.Judge("image", "holiday.JPEG", Jpeg).Warnings);
    }

    [Fact]
    public void The_rejection_reason_names_what_was_found_and_what_was_expected()
    {
        var v = ContentSniffer.Judge("image", "a.jpg", Pdf);
        Assert.Contains("pdf", v.Reason);
        Assert.Contains("'image'", v.Reason);
    }
}

public sealed class PerceptualHashTests
{
    private static byte[] Image(Func<int, int, double> pixel) =>
        Enumerable.Range(0, 32 * 32).Select(i => (byte)Math.Clamp((int)pixel(i % 32, i / 32), 0, 255)).ToArray();

    private static readonly byte[] Gradient = Image((x, y) => 8 * x + 2 * y);
    private static readonly byte[] Checker = Image((x, y) => ((x / 4 + y / 4) % 2) * 255);
    private static readonly byte[] Circle = Image((x, y) => Math.Sqrt((x - 16) * (x - 16) + (y - 16) * (y - 16)) < 10 ? 230 : 25);

    [Fact]
    public void The_same_image_always_gives_the_same_hash() =>
        Assert.Equal(PerceptualHash.FromGrayscale(Circle), PerceptualHash.FromGrayscale(Circle.ToArray()));

    [Fact]
    public void A_slightly_altered_image_is_close_and_different_images_are_far()
    {
        var rng = new Random(3);
        var noisy = Circle.Select(p => (byte)Math.Clamp(p + rng.Next(-6, 7), 0, 255)).ToArray();          // recompression noise
        var brighter = Circle.Select(p => (byte)Math.Min(255, p + 20)).ToArray();                          // colour/brightness tweak
        var h = PerceptualHash.FromGrayscale(Circle);
        Assert.InRange(PerceptualHash.Distance(h, PerceptualHash.FromGrayscale(noisy)), 0, 6);
        Assert.InRange(PerceptualHash.Distance(h, PerceptualHash.FromGrayscale(brighter)), 0, 6);
        Assert.True(PerceptualHash.Distance(h, PerceptualHash.FromGrayscale(Checker)) > 12);
        Assert.True(PerceptualHash.Distance(PerceptualHash.FromGrayscale(Gradient), PerceptualHash.FromGrayscale(Checker)) > 12);
    }

    [Fact]
    public void Distance_counts_differing_bits()
    {
        Assert.Equal(0, PerceptualHash.Distance(0xFF, 0xFF));
        Assert.Equal(64, PerceptualHash.Distance(0, ulong.MaxValue));
        Assert.Equal(3, PerceptualHash.Distance(0b1011, 0b0000));
    }

    [Fact]
    public void Hashes_round_trip_through_hex_and_bad_input_is_refused()
    {
        var h = PerceptualHash.FromGrayscale(Circle);
        var hex = PerceptualHash.ToHex(h);
        Assert.Equal(16, hex.Length);
        Assert.True(PerceptualHash.TryParse(hex, out var back));
        Assert.Equal(h, back);
        Assert.False(PerceptualHash.TryParse("xyz", out _));
        Assert.False(PerceptualHash.TryParse(null, out _));
        Assert.False(PerceptualHash.TryParse("0123456789abcdeg", out _));
        Assert.Throws<ArgumentException>(() => PerceptualHash.FromGrayscale(new byte[10]));
    }
}

public sealed class VersionRulesTests
{
    [Fact]
    public void A_versions_id_and_storage_key_come_only_from_its_upload_so_repeating_promotion_finds_the_same_version()
    {
        var upload = Guid.NewGuid();
        Assert.Equal(AssetVersion.IdFor(upload), AssetVersion.IdFor(upload));
        Assert.NotEqual(AssetVersion.IdFor(upload), AssetVersion.IdFor(Guid.NewGuid()));
        var (t, a) = (Guid.NewGuid(), Guid.NewGuid());
        var v = AssetVersion.Create(t, a, 1, "originals", upload, 10, "image/jpeg", new string('a', 64), [], "", null, DateTimeOffset.UtcNow);
        Assert.Equal($"{t:N}/{a:N}/{v.Id:N}", v.StorageKey);
        Assert.DoesNotContain("..", v.StorageKey);
    }

    [Fact]
    public void An_upload_walks_the_ingest_stages_in_order()
    {
        var u = Upload.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a.jpg", 10, "image/jpeg", "b", "s3", 5, false, null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        u.MarkProcessing(DateTimeOffset.UtcNow);
        Assert.False(u.IsAt("hashed"));
        u.MarkReady(new string('a', 64), 10);
        Assert.True(u.IsAt("hashed"));
        u.MarkScanned(new() { ["scan"] = System.Text.Json.JsonSerializer.SerializeToElement("clean") });
        Assert.True(u.IsAt("scanned"));
        Assert.False(u.IsAt("hashed")); // a repeated message for an earlier stage is recognised as already done
        u.MarkSniffed("image/jpeg", []);
        Assert.Equal("image/jpeg", u.DetectedMime);
        u.MarkExtracted(new() { ["image"] = System.Text.Json.JsonSerializer.SerializeToElement(new { width = 1 }) });
        Assert.Equal(2, u.TechnicalMetadata.Count); // metadata accumulates across stages
        u.MarkIngested();
        Assert.Equal(UploadStatus.Ingested, u.Status);
        Assert.False(u.IsAt("extracted"));
    }

    [Fact]
    public void Blocking_and_rejecting_end_the_upload_with_a_reason()
    {
        var u = Upload.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a.jpg", 10, "x", "b", "s3", 5, false, null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        u.MarkReady(new string('a', 64), 10);
        u.MarkBlocked("Virus detected: Eicar-Test-Signature");
        Assert.Equal(UploadStatus.Blocked, u.Status);
        Assert.Contains("Eicar", u.Error);
        var v = Upload.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a.jpg", 10, "x", "b", "s3", 5, false, null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        v.MarkRejected("not an image");
        Assert.Equal(UploadStatus.Failed, v.Status);
        Assert.Equal("rejected", v.IngestStage);
    }
}
