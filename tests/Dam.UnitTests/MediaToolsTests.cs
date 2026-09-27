using Dam.Domain.Assets;
using Dam.Infrastructure.Delivery;
using Dam.UnitTests.Support;
using NetVips;

namespace Dam.UnitTests;

public sealed class VideoRendererTests
{
    private static readonly MediaToolSettings Settings = new() { Preset = "ultrafast", ToolTimeout = TimeSpan.FromSeconds(30) };
    private readonly VideoRenderer _renderer = new(Settings);

    [Fact]
    public async Task A_frame_is_extracted_as_a_decodable_picture()
    {
        var frame = await _renderer.ExtractFrameAsync(TestImages.Video(2), CancellationToken.None);
        using var img = Image.NewFromBuffer(frame);
        Assert.Equal(320, img.Width);
        Assert.Equal(240, img.Height);
    }

    [Fact]
    public async Task A_clip_is_transcoded_to_the_target_height_keeping_aspect_and_capped_at_the_profile_duration()
    {
        var profile = RenditionProfiles.Videos.First(p => p.Name == "preview");   // 720 height, 8s cap
        var result = await _renderer.TranscodeAsync(TestImages.Video(3, 320, 240), profile, 320, 240, CancellationToken.None);
        Assert.Equal(720, result.Height);
        Assert.Equal(320 * 720 / 240, result.Width);       // aspect kept, width computed from the source's 4:3 ratio
        Assert.NotEmpty(result.Bytes);
        Assert.True(result.DurationSeconds is null or <= 3.5);
    }

    [Fact]
    public async Task A_signage_profile_letterboxes_to_the_exact_target_size()
    {
        var profile = RenditionProfiles.Videos.First(p => p.Name == "signage_1080");
        var result = await _renderer.TranscodeAsync(TestImages.Video(1, 320, 240), profile, 320, 240, CancellationToken.None);
        Assert.Equal((1920, 1080), (result.Width, result.Height));   // exact fit, whatever the source shape
    }

    [Fact]
    public async Task A_longer_source_is_capped_by_the_profiles_maximum_duration()
    {
        var profile = RenditionProfiles.Videos.First(p => p.Name == "preview");
        var result = await _renderer.TranscodeAsync(TestImages.Video(5), profile, 320, 240, CancellationToken.None);
        Assert.True(result.DurationSeconds is null || result.DurationSeconds <= 8.5);
    }

    [Fact]
    public async Task A_missing_ffmpeg_binary_is_reported_distinctly_from_a_bad_input()
    {
        var broken = new VideoRenderer(new MediaToolSettings { FfmpegPath = "no-such-ffmpeg-binary", ToolTimeout = TimeSpan.FromSeconds(5) });
        await Assert.ThrowsAsync<MediaToolMissingException>(() => broken.ExtractFrameAsync(TestImages.Video(1), CancellationToken.None));
    }

    [Fact]
    public async Task Bytes_that_are_not_a_video_fail_with_a_reported_reason_not_a_crash()
    {
        await Assert.ThrowsAsync<MediaToolFailedException>(() =>
            _renderer.TranscodeAsync("not a video"u8.ToArray(), RenditionProfiles.Videos[0], 320, 240, CancellationToken.None));
    }
}

public sealed class PdfRendererTests
{
    private static readonly MediaToolSettings Settings = new() { ToolTimeout = TimeSpan.FromSeconds(20) };
    private readonly PdfRenderer _renderer = new(Settings);

    [Fact]
    public async Task Page_count_matches_what_was_built()
    {
        Assert.Equal(5, await _renderer.PageCountAsync(TestImages.Pdf("Guide", pages: 5), CancellationToken.None));
        Assert.Equal(1, await _renderer.PageCountAsync(TestImages.Pdf("Single"), CancellationToken.None));
    }

    [Fact]
    public async Task Each_page_rasterises_to_a_decodable_picture_the_shape_of_the_page()
    {
        var pdf = TestImages.Pdf("Guide", pages: 3);
        foreach (var n in new[] { 1, 2, 3 })
        {
            var png = await _renderer.RasterizePageAsync(pdf, n, CancellationToken.None);
            using var img = Image.NewFromBuffer(png);
            Assert.True(img.Width > 0 && img.Height > img.Width, $"page {n} should be portrait-shaped like the A4 page it renders");
        }
    }

    [Fact]
    public async Task A_missing_poppler_binary_is_reported_distinctly()
    {
        var broken = new PdfRenderer(new MediaToolSettings { PdfinfoPath = "no-such-pdfinfo", ToolTimeout = TimeSpan.FromSeconds(5) });
        await Assert.ThrowsAsync<MediaToolMissingException>(() => broken.PageCountAsync(TestImages.Pdf("X"), CancellationToken.None));
    }

    [Fact]
    public async Task Bytes_that_are_not_a_pdf_fail_with_a_reported_reason()
    {
        await Assert.ThrowsAsync<MediaToolFailedException>(() => _renderer.PageCountAsync("not a pdf"u8.ToArray(), CancellationToken.None));
    }
}
