using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Infrastructure.Ingest;
using Dam.UnitTests.Support;

namespace Dam.UnitTests;

/// <summary>A pretend clamd: reads the INSTREAM framing exactly as the real daemon does and replies with a chosen verdict.</summary>
internal sealed class FakeClamd(Func<byte[], string> verdict) : Stream
{
    private readonly MemoryStream _received = new();
    private MemoryStream? _reply;
    public byte[] Content { get; private set; } = [];
    public string? Command { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _received.Write(buffer, offset, count);

    public override int Read(byte[] buffer, int offset, int count)
    {
        _reply ??= Decode();
        return _reply.Read(buffer, offset, count);
    }

    private MemoryStream Decode()
    {
        var data = _received.ToArray();
        var nul = Array.IndexOf(data, (byte)0);
        Command = Encoding.ASCII.GetString(data, 0, nul);
        var content = new MemoryStream();
        var pos = nul + 1;
        while (true)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            pos += 4;
            if (len == 0) break;
            content.Write(data, pos, len);
            pos += len;
        }
        Content = content.ToArray();
        return new MemoryStream(Encoding.ASCII.GetBytes(verdict(Content) + "\0"));
    }
}

public sealed class ClamAvProtocolTests
{
    [Fact]
    public async Task Content_is_sent_in_length_prefixed_chunks_after_the_INSTREAM_command_and_a_clean_reply_is_understood()
    {
        var data = new byte[3 * 1024 * 1024 + 123]; new Random(1).NextBytes(data);   // more than one 1 MB chunk
        var clamd = new FakeClamd(_ => "stream: OK");
        var outcome = await ClamAvProtocol.ScanAsync(clamd, new MemoryStream(data), default);
        Assert.Equal("zINSTREAM", clamd.Command);
        Assert.Equal(data, clamd.Content);        // every byte arrived, in order, exactly once
        Assert.Equal(ScanVerdict.Clean, outcome.Verdict);
    }

    [Fact]
    public async Task A_found_reply_becomes_an_infected_verdict_with_the_signature_name()
    {
        var outcome = await ClamAvProtocol.ScanAsync(new FakeClamd(_ => "stream: Win.Test.EICAR_HDB-1 FOUND"), new MemoryStream(TestImages.Eicar()), default);
        Assert.Equal(ScanVerdict.Infected, outcome.Verdict);
        Assert.Equal("Win.Test.EICAR_HDB-1", outcome.Signature);
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("")]
    [InlineData("stream: something odd")]
    public async Task Anything_that_is_not_a_clear_verdict_is_an_error_so_ingest_fails_closed(string reply) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClamAvProtocol.ScanAsync(new FakeClamd(_ => reply), new MemoryStream(new byte[10]), default));

    [Fact]
    public async Task A_file_over_the_scan_limit_is_skipped_and_says_so_without_contacting_clamd()
    {
        var scanner = new ClamAvScanner(new ScanSettings { MaxBytes = 100, Host = "203.0.113.1", Port = 1 }); // unroutable: would hang if contacted
        var outcome = await scanner.ScanAsync(new MemoryStream(new byte[10]), length: 101, default);
        Assert.Equal(ScanVerdict.Skipped, outcome.Verdict);
        Assert.StartsWith("oversize", outcome.Note);
    }

    [Fact]
    public async Task An_unreachable_scanner_throws_and_is_never_read_as_clean()
    {
        var scanner = new ClamAvScanner(new ScanSettings { Host = "127.0.0.1", Port = 1, Timeout = TimeSpan.FromSeconds(5) });
        await Assert.ThrowsAnyAsync<Exception>(() => scanner.ScanAsync(new MemoryStream(new byte[10]), 10, default));
    }

    [Fact]
    public async Task A_disabled_scanner_records_that_nothing_was_scanned()
    {
        var o = await new DisabledScanner().ScanAsync(Stream.Null, 0, default);
        Assert.Equal(ScanVerdict.Skipped, o.Verdict);
        Assert.Contains("disabled", o.Note);
    }
}

public sealed class FfprobeSummaryTests
{
    [Fact]
    public void ffprobe_output_is_reduced_to_a_compact_stable_shape()
    {
        var raw = JsonDocument.Parse("""
            {"streams":[
              {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30000/1001","bit_rate":"8000000","pix_fmt":"yuv420p"},
              {"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","bit_rate":"128000"},
              {"codec_type":"data","codec_name":"bin_data"}],
             "format":{"format_name":"mov,mp4,m4a","duration":"12.3456","bit_rate":"8200000","filename":"secret-signed-url"}}
            """).RootElement;
        var s = FfprobeMediaProbe.Summarise(raw);

        Assert.Equal("mov,mp4,m4a", s.GetProperty("formatName").GetString());
        Assert.Equal(12.346, s.GetProperty("durationSeconds").GetDouble());
        var streams = s.GetProperty("streams");
        Assert.Equal(2, streams.GetArrayLength());                                  // the data stream is dropped
        Assert.Equal(1920, streams[0].GetProperty("width").GetInt32());
        Assert.Equal(29.97, streams[0].GetProperty("frameRate").GetDouble());
        Assert.Equal(48000, streams[1].GetProperty("sampleRate").GetInt32());
        Assert.DoesNotContain("secret-signed-url", s.GetRawText());                  // the presigned URL must not leak into stored metadata
    }

    [Fact]
    public void Missing_fields_are_simply_left_out()
    {
        var s = FfprobeMediaProbe.Summarise(JsonDocument.Parse("""{"streams":[{"codec_type":"audio","codec_name":"mp3"}],"format":{}}""").RootElement);
        Assert.False(s.TryGetProperty("durationSeconds", out _));
        Assert.False(s.GetProperty("streams")[0].TryGetProperty("width", out _));
    }
}

public sealed class ImageHasherTests
{
    private static ulong Hash(byte[] image) => ImageHasher.HashOf(new MemoryStream(image)) ?? throw new InvalidOperationException("not decodable");

    [Fact]
    public void A_resized_and_recompressed_copy_hashes_close_to_the_original_and_a_different_picture_hashes_far()
    {
        var disc = TestImages.Disc();
        var copy = TestImages.Resize(disc, 96);                 // smaller and re-encoded
        var png = TestImages.Disc(format: ".png");              // same picture, different format
        var checker = TestImages.Checkerboard();

        Assert.InRange(PerceptualHash.Distance(Hash(disc), Hash(copy)), 0, 8);
        Assert.InRange(PerceptualHash.Distance(Hash(disc), Hash(png)), 0, 8);
        Assert.True(PerceptualHash.Distance(Hash(disc), Hash(checker)) > 14);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_give_no_hash_instead_of_crashing()
    {
        Assert.Null(ImageHasher.HashOf(new MemoryStream(Encoding.ASCII.GetBytes("this is not a picture at all"))));
        Assert.Null(ImageHasher.HashOf(new MemoryStream(TestImages.Disc()[..200])));   // a truncated JPEG
    }
}

/// <summary>Just enough of a store for the extractor: objects live in memory.</summary>
internal sealed class MemoryStore(byte[] content) : IObjectStore
{
    public string QuarantineBucket => "q";
    public string OriginalsBucket => "o";
    public string RenditionsBucket => "r";
    public Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken ct) => throw new NotSupportedException();
    public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, long length, CancellationToken ct) =>
        Task.FromResult(content.Skip((int)offset).Take((int)length).ToArray());
    public Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(content));
    public Task<string> StartMultipartAsync(string bucket, string key, string contentType, CancellationToken ct) => throw new NotSupportedException();
    public string PresignPart(string bucket, string key, string s3UploadId, int partNumber, TimeSpan ttl) => throw new NotSupportedException();
    public Task<IReadOnlyList<StoredPart>> ListPartsAsync(string bucket, string key, string s3UploadId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ObjectInfo> CompleteMultipartAsync(string bucket, string key, string s3UploadId, IEnumerable<StoredPart> parts, CancellationToken ct) => throw new NotSupportedException();
    public Task AbortMultipartAsync(string bucket, string key, string s3UploadId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ObjectInfo?> HeadAsync(string bucket, string key, CancellationToken ct) => throw new NotSupportedException();
    public Task CopyAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, long size, CancellationToken ct) => throw new NotSupportedException();
    public string PresignGet(string bucket, string key, TimeSpan ttl) => throw new NotSupportedException();
    public Task DeleteAsync(string bucket, string key, CancellationToken ct) => throw new NotSupportedException();
    public string PresignPut(string bucket, string key, string contentType, TimeSpan ttl) => throw new NotSupportedException();
    public string PresignDownload(string bucket, string key, string fileName, TimeSpan ttl) => throw new NotSupportedException();
}

internal sealed class NoProbe : IMediaProbe
{
    public Task<JsonElement?> ProbeAsync(string bucket, string key, CancellationToken ct) => Task.FromResult<JsonElement?>(null);
}

public sealed class TechnicalMetadataExtractorTests
{
    private static Task<Dictionary<string, JsonElement>> Extract(byte[] file, ContentKind kind, string format, string name) =>
        new TechnicalMetadataExtractor(new MemoryStore(file), new NoProbe()).ExtractAsync(kind, format, "b", "k", file.Length, name, default);

    [Fact]
    public async Task A_jpeg_yields_its_size_and_the_camera_details_embedded_in_it()
    {
        var file = TestImages.WithExif(TestImages.Disc(320), "AcmeCam", "Model 7");
        var m = await Extract(file, ContentKind.Image, "jpeg", "shot.jpg");
        var image = m["image"];
        Assert.Equal(320, image.GetProperty("width").GetInt32());
        Assert.Equal(320, image.GetProperty("height").GetInt32());
        Assert.Equal("AcmeCam", image.GetProperty("exif").GetProperty("Make").GetString());
        Assert.Equal("Model 7", image.GetProperty("exif").GetProperty("Model").GetString());
        Assert.Equal("ok", m["extraction"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_png_without_camera_metadata_still_reports_its_size_without_a_warning()
    {
        var m = await Extract(TestImages.Disc(128, format: ".png"), ContentKind.Image, "png", "a.png");
        Assert.Equal(128, m["image"].GetProperty("width").GetInt32());
        Assert.False(m["image"].TryGetProperty("exif", out var exif) && exif.TryGetProperty("Make", out _));   // libvips writes default orientation, but no camera
        Assert.Equal("ok", m["extraction"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_pdf_yields_page_count_title_and_page_size()
    {
        var m = await Extract(TestImages.Pdf("Owner guide"), ContentKind.Pdf, "pdf", "guide.pdf");
        var pdf = m["pdf"];
        Assert.Equal(1, pdf.GetProperty("pages").GetInt32());
        Assert.Equal("Owner guide", pdf.GetProperty("title").GetString());
        Assert.Equal("Acme Docs", pdf.GetProperty("author").GetString());
        Assert.Equal(595, pdf.GetProperty("pageWidthPt").GetDouble());
    }

    [Fact]
    public async Task A_docx_yields_its_document_properties()
    {
        var m = await Extract(TestImages.Docx("Service plan", "Ada"), ContentKind.Archive, "zip", "plan.docx");
        var doc = m["document"];
        Assert.Equal("Service plan", doc.GetProperty("title").GetString());
        Assert.Equal("Ada", doc.GetProperty("author").GetString());
        Assert.Equal("3", doc.GetProperty("pages").GetString());
        Assert.Equal("Acme Writer", doc.GetProperty("application").GetString());
    }

    [Fact]
    public async Task A_damaged_file_gives_a_warning_and_never_throws_so_ingest_can_continue()
    {
        var m = await Extract(Encoding.ASCII.GetBytes("%PDF-1.4 this is not really a pdf"), ContentKind.Pdf, "pdf", "broken.pdf");
        Assert.Equal("partial", m["extraction"].GetProperty("status").GetString());
        Assert.NotEmpty(m["extraction"].GetProperty("notes").EnumerateArray());
        var broken = await Extract(new byte[] { 0xFF, 0xD8, 0xFF, 0x00, 0x01 }, ContentKind.Image, "jpeg", "x.jpg");
        Assert.Equal("partial", broken["extraction"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Video_without_ffprobe_is_reported_as_a_note_not_an_error()
    {
        var m = await Extract(new byte[100], ContentKind.Video, "mp4", "clip.mp4");
        Assert.Equal("partial", m["extraction"].GetProperty("status").GetString());
        Assert.Contains("ffprobe", m["extraction"].GetProperty("notes")[0].GetString());
    }

    [Fact]
    public async Task Kinds_without_technical_extraction_still_succeed()
    {
        var m = await Extract("a,b\n1,2\n"u8.ToArray(), ContentKind.Text, "text", "data.csv");
        Assert.Equal("ok", m["extraction"].GetProperty("status").GetString());
        Assert.Single(m);
    }

    [Fact]
    public void The_eicar_test_string_is_assembled_correctly_at_run_time()
    {
        var text = Encoding.ASCII.GetString(TestImages.Eicar());
        Assert.Equal(68, text.Length);
        Assert.EndsWith("EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*", text, StringComparison.Ordinal);
    }
}
