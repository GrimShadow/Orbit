using System.Net;
using System.Text.Json;
using Dam.Domain.Assets;
using Dam.UnitTests.Support;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class OfficeRenditionTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly Dictionary<string, string?> NoScan = new() { ["DAM_SCAN_MODE"] = "off" };   // scanning has its own tests

    private static byte[] Fixture(string ext) => ext switch
    {
        "docx" => TestImages.Docx("Service plan", "Ada"),
        "pptx" => TestImages.Pptx("Campaign deck", "Ada"),
        _ => TestImages.Xlsx("Quarterly totals", "Ada"),
    };

    private async Task<Guid> IngestedOffice(string admin, Guid folder, string ext, string? fileName = null, bool publish = true)
    {
        var asset = await NewAsset(admin, folder, type: "document");
        var u = await Ingest(admin, asset, Fixture(ext), fileName ?? $"doc.{ext}", "application/octet-stream");
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        if (publish) Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        return asset;
    }

    private async Task<JsonElement> Link(string token, Guid asset, object body)
    {
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", token, body);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body;
    }

    private static string Url(JsonElement link, string name) => new Uri(link.GetProperty("links").GetProperty(name).GetString()!).PathAndQuery;

    private async Task<JsonElement> RenditionsOf(string admin, Guid asset, int expected, int seconds = 40)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            var list = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body;
            if (list.GetArrayLength() >= expected) return list;
            await Task.Delay(200);
        }
        throw new TimeoutException("Renditions did not finish generating.");
    }

    // ---- content over names: the real format wins, whatever the file is called ------------------------------------

    [Theory]
    [InlineData("docx", OfficeMime.Docx)]
    [InlineData("pptx", OfficeMime.Pptx)]
    [InlineData("xlsx", OfficeMime.Xlsx)]
    public async Task Each_office_format_is_identified_from_its_own_zip_entries_not_the_file_name(string ext, string expectedMime)
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await NewAsset(admin, folder, type: "document");
        var u = await Ingest(admin, asset, Fixture(ext), $"mislabeled.{(ext == "docx" ? "xlsx" : "docx")}");   // wrong extension on purpose
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        var version = (await Versions(admin, asset))[0];
        Assert.Equal(expectedMime, version.GetProperty("mime").GetString());
    }

    // ---- eager renditions: page-1 stills plus every page, exactly like a PDF ------------------------------------

    [Theory]
    [InlineData("docx")]
    [InlineData("pptx")]
    [InlineData("xlsx")]
    public async Task An_office_document_gets_thumb_preview_and_its_page_rendered_by_converting_to_pdf_first(string ext)
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedOffice(admin, folder, ext, publish: false);

        var list = await RenditionsOf(admin, asset, 3);
        var byProfile = list.EnumerateArray().Where(r => r.GetProperty("pageNo").ValueKind == JsonValueKind.Null).ToDictionary(r => r.GetProperty("profile").GetString()!);
        Assert.Contains("thumb", byProfile.Keys);
        Assert.Contains("preview", byProfile.Keys);
        Assert.Equal("image/webp", byProfile["thumb"].GetProperty("mime").GetString());

        var pages = list.EnumerateArray().Where(r => r.GetProperty("profile").GetString() == "pdf_pages").ToList();
        Assert.Single(pages);
        Assert.Equal(1, pages[0].GetProperty("pageNo").GetInt32());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'ingest.scanned'"));   // sanity the pipeline ran once, not per-profile
    }

    // ---- delivery -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Office_renditions_are_served_through_the_same_signed_links_as_a_pdf()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedOffice(admin, folder, "docx");
        await RenditionsOf(admin, asset, 3);

        var http = await Delivery();
        var link = await Link(admin, asset, new { renditions = new[] { "thumb", "preview", "pdf_pages-1" } });
        foreach (var name in new[] { "thumb", "preview", "pdf_pages-1" })
        {
            var res = await http.GetAsync(Url(link, name));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/webp", res.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task An_unpublished_office_documents_stills_are_watermarked_like_any_other_document()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedOffice(admin, folder, "pptx", publish: false);
        var link = await Link(admin, asset, new { renditions = new[] { "preview" } });
        Assert.True(link.GetProperty("watermarked").GetBoolean());
        var res = await (await Delivery()).GetAsync(Url(link, "preview"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("private", res.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_page_is_converted_and_rendered_on_demand_when_it_was_not_pre_generated()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan, renditions: false); await worker.StartAsync();   // no eager rendering
        var asset = await IngestedOffice(admin, folder, "xlsx");
        Assert.Equal(0, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body.GetArrayLength());

        var link = await Link(admin, asset, new { renditions = new[] { "thumb" } });
        var http = await Delivery();
        var first = await http.GetAsync(Url(link, "thumb"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("miss", first.Headers.GetValues("X-Dam-Cache").Single());
        var second = await http.GetAsync(Url(link, "thumb"));
        Assert.Equal("hit", second.Headers.GetValues("X-Dam-Cache").Single());
    }

    [Fact]
    public async Task A_corrupt_office_document_ingests_but_simply_gets_no_renditions()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await NewAsset(admin, folder, type: "document");
        // Genuinely a docx by content (word/document.xml marker included via the fixture's zip entries) but truncated, so it
        // ingests fine (scan/sniff/promote never open the file as a zip) and only rendering fails.
        var bytes = TestImages.Docx("Broken", "Ada");
        var truncated = bytes[..(bytes.Length / 2)];
        var u = await Ingest(admin, asset, truncated, "broken.docx");
        Assert.Equal("ingested", u.GetProperty("status").GetString());

        // give the (failing) rendition consumer a moment, then confirm nothing was produced
        await Task.Delay(3000);
        Assert.Equal(0, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body.GetArrayLength());
    }
}
