using System.Net;
using System.Text;
using System.Text.Json;
using Dam.Domain.Assets;
using Dam.UnitTests.Support;
using NetVips;

namespace Dam.IntegrationTests;

/// <summary>What a licence does to delivery (ADR-0033): links refused outside the licence, the channel claim, the checks made again when a link is used, and the watermark a licence can require.</summary>
[Collection("pg")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in DisposeAsync (xUnit IAsyncLifetime).")]
public sealed class RightsDeliveryTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly Dictionary<string, string?> NoScan = new() { ["DAM_SCAN_MODE"] = "off" };
    private static readonly Dictionary<string, string?> VideoScan = new(NoScan) { ["DAM_FFPROBE_PATH"] = "ffprobe" };
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static int _picture;
    private static byte[] NewPicture(int w = 800, int h = 500) => TestImages.Sized(w, h, radiusFraction: 0.15 + (Interlocked.Increment(ref _picture) % 500) * 0.0005);
    private static Image Decode(byte[] b) => Image.NewFromBuffer(b);

    private async Task<Guid> PublishedImage(string admin, Guid folder, string[]? channels = null, string[]? regions = null)
    {
        var asset = await NewAsset(admin, folder, metadata: new { description = new { en = "A car" }, channels = channels ?? ["web", "signage"], regions = regions ?? ["north"] });
        Assert.Equal("ingested", (await Ingest(admin, asset, NewPicture(), "photo.jpg")).GetProperty("status").GetString());
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        return asset;
    }

    private async Task<Guid> NewRights(string admin, string name = "Acme stock licence", string[]? channels = null, string[]? regions = null, DateOnly? from = null, DateOnly? to = null, bool watermark = false)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/rights", admin, new
        {
            name, licenceType = "licensed", licensor = "Acme", channels = channels ?? [], regions = regions ?? [], validFrom = from, validTo = to, watermarkRequired = watermark, notes = "",
        });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Body.GetProperty("id").GetGuid();
    }

    private async Task Assign(string admin, Guid asset, Guid rights) =>
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = rights })).Status);

    private async Task<(HttpStatusCode Status, JsonElement Body)> LinkRequest(string token, Guid asset, object body) => await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", token, body);

    private async Task<string> ThumbUrl(string token, Guid asset, string? channel = null, string? region = null, string rendition = "thumb")
    {
        var r = await LinkRequest(token, asset, new { renditions = new[] { rendition }, channel, region });
        Assert.True(r.Status == HttpStatusCode.OK, r.Body.ToString());
        return PathOf(r.Body.GetProperty("links").GetProperty(rendition).GetString()!);
    }

    private static JsonElement ClaimsOf(string url)
    {
        var body = url.Split('/')[2].Split('.')[0].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=')))).RootElement.Clone();
    }

    // ---- links ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_link_for_a_channel_or_region_the_licence_does_not_cover_is_refused_with_the_reason_and_one_it_covers_records_them_in_the_signed_token()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder, ["web", "signage"], ["north", "south"]);
        await Assign(admin, asset, await NewRights(admin, channels: ["web"], regions: ["north"]));

        var wrongChannel = await LinkRequest(admin, asset, new { renditions = new[] { "thumb" }, channel = "signage" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongChannel.Status);
        Assert.Equal("rights_restricted", wrongChannel.Body.GetProperty("code").GetString());
        Assert.Contains("does not cover the 'signage' channel", wrongChannel.Body.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, wrongChannel.Body.GetProperty("reasons").GetArrayLength());
        Assert.Equal(wrongChannel.Body.GetProperty("reason").GetString(), wrongChannel.Body.GetProperty("title").GetString());

        var both = await LinkRequest(admin, asset, new { renditions = new[] { "thumb" }, channel = "signage", region = "south" });
        Assert.Equal(HttpStatusCode.Forbidden, both.Status);
        Assert.Equal(2, both.Body.GetProperty("reasons").GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await LinkRequest(admin, asset, new { renditions = new[] { "thumb" }, channel = "web", region = "south" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkRequest(admin, asset, new { channel = "two words" })).Status);

        var url = await ThumbUrl(admin, asset, "WEB", "North");
        var claims = ClaimsOf(url);
        Assert.Equal(("web", "north"), (claims.GetProperty("C").GetString(), claims.GetProperty("R").GetString()));   // normalised, and signed
        Assert.Equal(HttpStatusCode.OK, (await (await Delivery()).GetAsync(url)).StatusCode);

        // a link that does not say what it is for is not asked the question, and carries no claim
        var plain = ClaimsOf(await ThumbUrl(admin, asset));
        Assert.False(plain.TryGetProperty("C", out _));
        Assert.False(plain.TryGetProperty("R", out _));
    }

    [Fact]
    public async Task A_link_is_refused_for_a_channel_the_assets_own_metadata_excludes_even_without_rights()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder, ["web"], []);
        var r = await LinkRequest(admin, asset, new { channel = "signage" });
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Equal("This asset is not available for the 'signage' channel.", r.Body.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await LinkRequest(admin, asset, new { channel = "web" })).Status);
    }

    [Fact]
    public async Task An_ended_licence_refuses_new_links_and_stops_links_already_out_even_while_the_asset_is_still_published()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder);
        var rights = await NewRights(admin, to: Today.AddDays(10));
        await Assign(admin, asset, rights);
        var http = await Delivery();
        var url = await ThumbUrl(admin, asset);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url)).StatusCode);

        Sql($"UPDATE rights SET valid_to = '{Today.AddDays(-1):yyyy-MM-dd}' WHERE id = '{rights}'");   // time passes; the expiry job has not run
        Assert.Equal("published", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("status").GetString());
        var refused = await LinkRequest(admin, asset, new { renditions = new[] { "thumb" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("ended on", refused.Body.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var gone = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);                                            // 410, like an asset that has expired
        Assert.Contains("rights_expired", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // the licence is extended: links work again, new and old
        Sql($"UPDATE rights SET valid_to = '{Today.AddDays(30):yyyy-MM-dd}' WHERE id = '{rights}'");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LinkRequest(admin, asset, new { renditions = new[] { "thumb" } })).Status);
    }

    [Fact]
    public async Task A_licence_that_has_not_begun_refuses_links_with_403_both_when_asked_for_and_when_used()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder);
        var rights = await NewRights(admin);
        await Assign(admin, asset, rights);
        var url = await ThumbUrl(admin, asset);
        Sql($"UPDATE rights SET valid_from = '{Today.AddDays(2):yyyy-MM-dd}' WHERE id = '{rights}'");

        var refused = await LinkRequest(admin, asset, new { renditions = new[] { "thumb" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("starts on", refused.Body.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var used = await (await Delivery()).GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, used.StatusCode);
        Assert.Contains("rights_not_valid", await used.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_channel_a_link_was_issued_for_is_checked_again_against_the_rights_as_they_are_when_it_is_used()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder, ["web", "signage"], []);
        var rights = await NewRights(admin, channels: ["web", "signage"]);
        await Assign(admin, asset, rights);
        var forWeb = await ThumbUrl(admin, asset, "web");
        var forSignage = await ThumbUrl(admin, asset, "signage");
        var anyone = await ThumbUrl(admin, asset);
        var http = await Delivery();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(forWeb)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", admin, new
            { name = "Acme stock licence", licenceType = "licensed", channels = new[] { "signage" }, regions = Array.Empty<string>(), watermarkRequired = false })).Status);
        var withdrawn = await http.GetAsync(forWeb);
        Assert.Equal(HttpStatusCode.Forbidden, withdrawn.StatusCode);
        Assert.Contains("rights_restricted", await withdrawn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(forSignage)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(anyone)).StatusCode);   // it never claimed a channel, so it is not asked
    }

    [Fact]
    public async Task A_token_signed_before_channels_existed_still_works()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder);
        var old = new DeliveryTokenSigner([ApiTestHost.SigningKey]).Sign(new DeliveryClaims(t, asset, "r:thumb", DateTimeOffset.UtcNow.AddHours(1), null, false));
        Assert.False(ClaimsOf($"/d/{old}/{asset}/thumb").TryGetProperty("C", out _));
        Assert.Equal(HttpStatusCode.OK, (await (await Delivery()).GetAsync($"/d/{old}/{asset}/thumb")).StatusCode);
    }

    // ---- watermarks ----------------------------------------------------------------------------------------------

    private static double Difference(byte[] a, byte[] b)
    {
        using var x = Decode(a);
        using var y = Decode(b);
        Assert.Equal((x.Width, x.Height), (y.Width, y.Height));
        using var diff = (x.Flatten().Colourspace(Enums.Interpretation.Bw) - y.Flatten().Colourspace(Enums.Interpretation.Bw)).Abs();
        return diff.Avg();
    }

    [Fact]
    public async Task A_licence_that_requires_a_watermark_watermarks_a_published_asset_new_links_and_old_ones()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder);
        var http = await Delivery();
        var oldLink = await ThumbUrl(admin, asset, rendition: "preview");
        var cleanRes = await http.GetAsync(oldLink);
        Assert.Contains("public", cleanRes.Headers.CacheControl!.ToString());
        var clean = await cleanRes.Content.ReadAsByteArrayAsync();

        var rights = await NewRights(admin, watermark: true);
        await Assign(admin, asset, rights);

        var link = await LinkRequest(admin, asset, new { renditions = new[] { "preview" } });
        Assert.True(link.Body.GetProperty("watermarked").GetBoolean());
        var marked = await http.GetAsync(PathOf(link.Body.GetProperty("links").GetProperty("preview").GetString()!));
        Assert.Contains("private", marked.Headers.CacheControl!.ToString());   // a watermarked copy is not for a shared cache
        Assert.True(Difference(clean, await marked.Content.ReadAsByteArrayAsync()) > 0.5, "the licensed copy should carry a watermark");

        // the link issued before the licence required it is watermarked now, too: the rights are read again when it is used
        var old = await http.GetAsync(oldLink);
        Assert.Contains("private", old.Headers.CacheControl!.ToString());
        Assert.True(Difference(clean, await old.Content.ReadAsByteArrayAsync()) > 0.5);

        // transforms follow the same rule
        var transform = await LinkRequest(admin, asset, new { transform = new { w = 300, h = 200, fit = "cover", fmt = "jpg", q = 80 } });
        Assert.True(transform.Body.GetProperty("watermarked").GetBoolean());
        Assert.Contains("private", (await http.GetAsync(PathOf(transform.Body.GetProperty("links").GetProperty("transform").GetString()!))).Headers.CacheControl!.ToString());

        // and when the requirement is dropped the next copy is clean again
        await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", admin, new { name = "Acme stock licence", licenceType = "licensed", watermarkRequired = false });
        var again = await http.GetAsync(oldLink);
        Assert.Contains("public", again.Headers.CacheControl!.ToString());
        Assert.True(Difference(clean, await again.Content.ReadAsByteArrayAsync()) < 0.01);
    }

    [Fact]
    public async Task The_original_cannot_be_linked_or_fetched_while_the_licence_requires_a_watermark()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder);
        var originalLink = await ThumbUrl(admin, asset, rendition: "original");      // fine while the asset is clean
        Assert.Equal(HttpStatusCode.OK, (await (await Delivery()).GetAsync(originalLink)).StatusCode);

        await Assign(admin, asset, await NewRights(admin, watermark: true));
        var refused = await LinkRequest(admin, asset, new { renditions = new[] { "original" } });
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("watermark-required", refused.Body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Delivery()).GetAsync(originalLink)).StatusCode);   // the link issued earlier no longer yields a clean copy
    }

    [Fact]
    public async Task Video_renditions_are_withheld_and_pdf_pages_are_not_found_while_the_licence_requires_a_watermark()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(VideoScan, renditions: false); await worker.StartAsync();
        var video = await NewAsset(admin, folder, type: "video");
        Assert.Equal("ingested", (await Ingest(admin, video, TestImages.Video(2), "clip.mp4", "video/mp4")).GetProperty("status").GetString());
        var pdf = await NewAsset(admin, folder, type: "document");
        Assert.Equal("ingested", (await Ingest(admin, pdf, TestImages.Pdf($"Guide {Guid.NewGuid():N}", 2), "guide.pdf", "application/pdf")).GetProperty("status").GetString());
        Sql($"UPDATE assets SET status = 'published' WHERE id IN ('{video}', '{pdf}')");
        var http = await Delivery();

        var videoLink = await ThumbUrl(admin, video, rendition: "preview");     // issued before the licence
        var pageLink = await ThumbUrl(admin, pdf, rendition: "pdf_pages-1");
        var licence = await NewRights(admin, watermark: true);
        await Assign(admin, video, licence);
        await Assign(admin, pdf, licence);

        var transcode = await LinkRequest(admin, video, new { renditions = new[] { "preview" } });
        Assert.Equal(HttpStatusCode.Conflict, transcode.Status);                                     // never a clean transcode of a restricted video
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(videoLink)).StatusCode);          // nor through a link issued earlier
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(pageLink)).StatusCode);           // pdf pages have no watermarked form
        Assert.Equal(HttpStatusCode.OK, (await LinkRequest(admin, video, new { renditions = new[] { "thumb", "poster" } })).Status);   // the stills are watermarked instead
    }

    [Fact]
    public async Task Thumbnail_batches_follow_the_licence_watermarked_when_required_and_absent_when_it_has_ended()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var plain = await PublishedImage(admin, folder);
        var marked = await PublishedImage(admin, folder);
        var ended = await PublishedImage(admin, folder);
        await Assign(admin, marked, await NewRights(admin, "Marked", watermark: true));
        var endedRights = await NewRights(admin, "Ending", to: Today.AddDays(3));
        await Assign(admin, ended, endedRights);
        Sql($"UPDATE rights SET valid_to = '{Today.AddDays(-1):yyyy-MM-dd}' WHERE id = '{endedRights}'");

        var batch = (await Call(HttpMethod.Post, "/api/v1/assets/links/batch", admin, new { assetIds = new[] { plain, marked, ended } })).Body;
        var http = await Delivery();
        var plainRes = await http.GetAsync(PathOf(batch.GetProperty(plain.ToString()).GetString()!));
        var markedRes = await http.GetAsync(PathOf(batch.GetProperty(marked.ToString()).GetString()!));
        Assert.Contains("public", plainRes.Headers.CacheControl!.ToString());
        Assert.Contains("private", markedRes.Headers.CacheControl!.ToString());
        Assert.Equal(JsonValueKind.Null, batch.GetProperty(ended.ToString()).ValueKind);
    }

    [Fact]
    public async Task An_unpublished_asset_is_still_watermarked_with_or_without_rights()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await PublishedImage(admin, folder);
        Sql($"UPDATE assets SET status = 'approved' WHERE id = '{asset}'");
        await Assign(admin, asset, await NewRights(admin));
        var link = await LinkRequest(admin, asset, new { renditions = new[] { "preview" } });
        Assert.True(link.Body.GetProperty("watermarked").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await LinkRequest(admin, asset, new { renditions = new[] { "original" } })).Status);
    }
}
