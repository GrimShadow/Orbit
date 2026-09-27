using System.Net;
using System.Net.Http.Headers;
using Dam.Domain.Assets;
using Dam.UnitTests.Support;
using NetVips;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class VideoAndPdfDeliveryTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly Dictionary<string, string?> NoScan = new() { ["DAM_SCAN_MODE"] = "off" };   // scanning has its own tests
    /// <summary>The pipeline base disables ffprobe by default (so ingest-extraction tests stay fast and deterministic); video
    /// renditions want it, to record a real duration.</summary>
    private static readonly Dictionary<string, string?> VideoScan = new(NoScan) { ["DAM_FFPROBE_PATH"] = "ffprobe" };

    private async Task<Guid> IngestedVideo(string admin, Guid folder, byte[]? file = null, bool publish = true)
    {
        var asset = await NewAsset(admin, folder, type: "video");
        var u = await Ingest(admin, asset, file ?? TestImages.Video(3), "clip.mp4", "video/mp4");
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        if (publish) Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        return asset;
    }

    private async Task<Guid> IngestedPdf(string admin, Guid folder, int pages = 1, bool publish = true)
    {
        var asset = await NewAsset(admin, folder, type: "document");
        var u = await Ingest(admin, asset, TestImages.Pdf($"Guide {Guid.NewGuid():N}", pages), "guide.pdf", "application/pdf");
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        if (publish) Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        return asset;
    }

    private async Task<System.Text.Json.JsonElement> Link(string token, Guid asset, object body)
    {
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", token, body);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body;
    }

    private static string Url(System.Text.Json.JsonElement link, string name) => new Uri(link.GetProperty("links").GetProperty(name).GetString()!).PathAndQuery;

    private async Task<System.Text.Json.JsonElement> RenditionsOf(string admin, Guid asset, int expected, int seconds = 30)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            var list = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body;
            if (list.GetArrayLength() >= expected) return list;
            await Task.Delay(200);
        }
        throw new TimeoutException("Renditions did not finish generating.");
    }

    // ---- video: eager generation --------------------------------------------------------------------------------

    [Fact]
    public async Task Every_video_profile_is_generated_a_poster_and_thumb_are_made_from_a_frame_and_signage_is_letterboxed_exactly()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(VideoScan); await worker.StartAsync();
        var asset = await IngestedVideo(admin, folder, TestImages.Video(3, 320, 240), publish: false);

        var list = await RenditionsOf(admin, asset, RenditionProfiles.VideoStills.Count + RenditionProfiles.Videos.Count, seconds: 60);
        var byProfile = list.EnumerateArray().ToDictionary(r => r.GetProperty("profile").GetString()!);
        Assert.Equal(
            RenditionProfiles.VideoStills.Select(p => p.Name).Concat(RenditionProfiles.Videos.Select(p => p.Name)).Order(),
            byProfile.Keys.Order());

        Assert.Equal("image/webp", byProfile["thumb"].GetProperty("mime").GetString());
        Assert.Equal((240, 180), (byProfile["thumb"].GetProperty("width").GetInt32(), byProfile["thumb"].GetProperty("height").GetInt32()));   // 320x240 source, longest edge 240
        Assert.Equal("image/webp", byProfile["poster"].GetProperty("mime").GetString());

        Assert.Equal("video/mp4", byProfile["preview"].GetProperty("mime").GetString());
        Assert.Equal(720, byProfile["preview"].GetProperty("height").GetInt32());
        Assert.True(byProfile["preview"].GetProperty("duration").GetDouble() <= 8.5, "the preview clip is capped at 8 seconds");
        Assert.Equal(720, byProfile["mobile"].GetProperty("height").GetInt32());
        Assert.True(byProfile["mobile"].GetProperty("duration").GetDouble() >= 2.5, "mobile is the full clip, not capped like the preview");

        Assert.Equal((1920, 1080), (byProfile["signage_1080"].GetProperty("width").GetInt32(), byProfile["signage_1080"].GetProperty("height").GetInt32()));
        Assert.Equal((3840, 2160), (byProfile["signage_4k"].GetProperty("width").GetInt32(), byProfile["signage_4k"].GetProperty("height").GetInt32()));

        Assert.Equal(RenditionProfiles.VideoStills.Count + RenditionProfiles.Videos.Count, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'rendition.ready'"));
    }

    // ---- video: delivery, range requests --------------------------------------------------------------------------

    [Fact]
    public async Task A_video_rendition_supports_byte_ranges_so_a_player_can_seek()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(VideoScan); await worker.StartAsync();
        var asset = await IngestedVideo(admin, folder);
        await RenditionsOf(admin, asset, RenditionProfiles.VideoStills.Count + RenditionProfiles.Videos.Count, seconds: 60);

        var url = Url(await Link(admin, asset, new { renditions = new[] { "mobile" } }), "mobile");
        var http = await Delivery();

        var whole = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal("bytes", whole.Headers.AcceptRanges.ToString());
        var full = await whole.Content.ReadAsByteArrayAsync();
        Assert.True(full.Length > 2000, "a real transcoded clip should be more than a couple of KB");

        var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new RangeHeaderValue(100, 199);
        var res = await http.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(100, res.Content.Headers.ContentLength);
        Assert.Equal($"bytes 100-199/{full.Length}", res.Content.Headers.ContentRange!.ToString());
        Assert.Equal(full.Skip(100).Take(100).ToArray(), await res.Content.ReadAsByteArrayAsync());

        var suffix = new HttpRequestMessage(HttpMethod.Get, url);
        suffix.Headers.Range = new RangeHeaderValue { Ranges = { new RangeItemHeaderValue(null, 50) } };  // "bytes=-50": the last 50 bytes
        var tail = await http.SendAsync(suffix);
        Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
        Assert.Equal(full[^50..], await tail.Content.ReadAsByteArrayAsync());

        var beyond = new HttpRequestMessage(HttpMethod.Get, url);
        beyond.Headers.Range = new RangeHeaderValue(full.Length + 100, full.Length + 200);
        Assert.Equal((HttpStatusCode)416, (await http.SendAsync(beyond)).StatusCode);
    }

    [Fact]
    public async Task An_unpublished_videos_still_images_are_watermarked_but_its_video_renditions_are_refused_not_leaked()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(VideoScan); await worker.StartAsync();
        var asset = await IngestedVideo(admin, folder, publish: false);

        var conflict = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", admin, new { renditions = new[] { "mobile" } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);

        var posterLink = await Link(admin, asset, new { renditions = new[] { "poster" } });
        Assert.True(posterLink.GetProperty("watermarked").GetBoolean());
        var res = await (await Delivery()).GetAsync(Url(posterLink, "poster"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/webp", res.Content.Headers.ContentType!.MediaType);
    }

    // ---- PDF pages -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_multi_page_pdf_gets_a_page_rendition_for_every_page_plus_thumb_and_preview_from_page_one()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedPdf(admin, folder, pages: 4, publish: false);

        var list = await RenditionsOf(admin, asset, 2 + 4);
        var byProfile = list.EnumerateArray().Where(r => r.GetProperty("pageNo").ValueKind == System.Text.Json.JsonValueKind.Null)
            .ToDictionary(r => r.GetProperty("profile").GetString()!);
        Assert.Contains("thumb", byProfile.Keys);
        Assert.Contains("preview", byProfile.Keys);

        var pages = list.EnumerateArray().Where(r => r.GetProperty("profile").GetString() == "pdf_pages").ToList();
        Assert.Equal(4, pages.Count);
        Assert.Equal(Enumerable.Range(1, 4), pages.Select(p => p.GetProperty("pageNo").GetInt32()).Order());
        foreach (var p in pages)
        {
            Assert.Equal("image/webp", p.GetProperty("mime").GetString());
            Assert.True(p.GetProperty("width").GetInt32() <= 1600);
        }
    }

    [Fact]
    public async Task Individual_pdf_pages_can_be_linked_and_fetched_and_a_page_past_the_end_is_not_found()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedPdf(admin, folder, pages: 3);
        await RenditionsOf(admin, asset, 2 + 3);

        var link = await Link(admin, asset, new { renditions = new[] { "pdf_pages-2" } });
        var http = await Delivery();
        var res = await http.GetAsync(Url(link, "pdf_pages-2"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/webp", res.Content.Headers.ContentType!.MediaType);

        var beyond = await Link(admin, asset, new { renditions = new[] { "pdf_pages-9" } });
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Url(beyond, "pdf_pages-9"))).StatusCode);
    }

    [Fact]
    public async Task A_pdf_page_is_generated_on_demand_when_it_was_not_pre_rendered()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan, renditions: false); await worker.StartAsync();     // no eager rendering at all
        var asset = await IngestedPdf(admin, folder, pages: 2);
        Assert.Equal(0, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body.GetArrayLength());

        var link = await Link(admin, asset, new { renditions = new[] { "pdf_pages-1" } });
        var http = await Delivery();
        var first = await http.GetAsync(Url(link, "pdf_pages-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("miss", first.Headers.GetValues("X-Dam-Cache").Single());
        var second = await http.GetAsync(Url(link, "pdf_pages-1"));
        Assert.Equal("hit", second.Headers.GetValues("X-Dam-Cache").Single());
    }

    [Fact]
    public async Task A_pdf_with_more_pages_than_the_configured_limit_only_renders_the_first_pages()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(new(NoScan) { ["DAM_PDF_MAX_PAGES"] = "2" }); await worker.StartAsync();
        var asset = await IngestedPdf(admin, folder, pages: 5);

        var list = await RenditionsOf(admin, asset, 2 + 2);
        var pages = list.EnumerateArray().Where(r => r.GetProperty("profile").GetString() == "pdf_pages").ToList();
        Assert.Equal(2, pages.Count);
    }
}
