using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using Dam.Domain.Assets;
using Dam.UnitTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetVips;

namespace Dam.IntegrationTests;

[Collection("pg")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in DisposeAsync (xUnit IAsyncLifetime).")]
public sealed class DeliveryTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly Dictionary<string, string?> NoScan = new() { ["DAM_SCAN_MODE"] = "off" };   // scanning has its own tests

    private static int _picture;
    /// <summary>A picture nobody has uploaded yet (identical bytes would be caught as a duplicate).</summary>
    private static byte[] NewPicture(int w = 800, int h = 500) => TestImages.Sized(w, h, radiusFraction: 0.15 + (Interlocked.Increment(ref _picture) % 500) * 0.0005);

    private async Task<Guid> IngestedImage(string admin, Guid folder, byte[]? file = null, bool publish = true)
    {
        var asset = await NewAsset(admin, folder);
        var u = await Ingest(admin, asset, file ?? NewPicture(), "photo.jpg");
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

    private static Image Decode(byte[] b) => Image.NewFromBuffer(b);

    // ---- renditions are made as soon as a version arrives ---------------------------------------------------

    [Fact]
    public async Task Every_image_profile_is_generated_when_a_version_arrives_and_never_larger_than_asked()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder, NewPicture(3000, 2000), publish: false);

        JsonElement list = default;
        for (var i = 0; i < 100; i++)
        {
            list = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body;
            if (list.GetArrayLength() == RenditionProfiles.Images.Count) break;
            await Task.Delay(200);
        }
        var byProfile = list.EnumerateArray().ToDictionary(r => r.GetProperty("profile").GetString()!);
        Assert.Equal(RenditionProfiles.Images.Select(p => p.Name).Order(), byProfile.Keys.Order());

        Assert.Equal((240, 160), (byProfile["thumb"].GetProperty("width").GetInt32(), byProfile["thumb"].GetProperty("height").GetInt32()));
        Assert.Equal((1024, 683), (byProfile["preview"].GetProperty("width").GetInt32(), byProfile["preview"].GetProperty("height").GetInt32()));
        Assert.Equal(1920, byProfile["web"].GetProperty("width").GetInt32());
        Assert.Equal("image/jpeg", byProfile["web_jpeg"].GetProperty("mime").GetString());
        Assert.Equal("image/webp", byProfile["web"].GetProperty("mime").GetString());
        Assert.Equal(1080, byProfile["mobile"].GetProperty("width").GetInt32());
        Assert.Equal(RenditionProfiles.Images.Count, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'rendition.ready'"));
        foreach (var p in RenditionProfiles.Images)
            Assert.True(await InStore("dam-renditions", $"{t:N}/{asset:N}/{Id(await Current(admin, asset), "id"):N}/{p.Name}.{RenditionProfiles.Extension(p.Format)}"));
    }

    private async Task<JsonElement> Current(string admin, Guid asset) => (await Versions(admin, asset))[0];

    // ---- delivery of named renditions ------------------------------------------------------------------------

    [Fact]
    public async Task A_signed_link_serves_the_rendition_with_cache_headers_and_answers_304_to_a_matching_etag()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var link = await Link(admin, asset, new { renditions = new[] { "thumb", "web" } });
        Assert.False(link.GetProperty("watermarked").GetBoolean());
        var thumbUrl = link.GetProperty("links").GetProperty("thumb").GetString()!;
        Assert.StartsWith("http://delivery.test/d/", thumbUrl);            // built on the configured public address

        var http = await Delivery();
        var res = await http.GetAsync(PathOf(thumbUrl));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/webp", res.Content.Headers.ContentType!.MediaType);
        using (var img = Decode(await res.Content.ReadAsByteArrayAsync())) Assert.Equal((240, 150), (img.Width, img.Height));
        Assert.Contains("public", res.Headers.CacheControl!.ToString());
        Assert.InRange(res.Headers.CacheControl!.MaxAge!.Value.TotalSeconds, 1, 300);
        Assert.True(res.Headers.Contains("X-Dam-Cache"));                    // lets an operator measure the hit ratio
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        var etag = res.Headers.ETag!.Tag;

        var again = new HttpRequestMessage(HttpMethod.Get, PathOf(thumbUrl));
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(again)).StatusCode);

        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, PathOf(thumbUrl)));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(etag, head.Headers.ETag!.Tag);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_rendition_that_was_not_pre_generated_is_made_on_first_request_then_served_from_cache()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan, renditions: false); await worker.StartAsync();    // no eager rendering
        var asset = await IngestedImage(admin, folder);
        Assert.Equal(0, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body.GetArrayLength());

        var url = PathOf((await Link(admin, asset, new { renditions = new[] { "preview" } })).GetProperty("links").GetProperty("preview").GetString()!);
        var http = await Delivery();
        var first = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("miss", first.Headers.GetValues("X-Dam-Cache").Single());
        var second = await http.GetAsync(url);
        Assert.Equal("hit", second.Headers.GetValues("X-Dam-Cache").Single());
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body.GetArrayLength());   // recorded for next time
        Assert.Equal(1, Count(t, "SELECT count(*) FROM renditions WHERE profile = 'preview'"));
    }

    [Fact]
    public async Task A_new_version_is_served_after_the_next_link_and_the_etag_changes()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var http = await Delivery();
        async Task<HttpResponseMessage> Thumb() => await http.GetAsync(PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" } })).GetProperty("links").GetProperty("thumb").GetString()!));

        var v1 = await Thumb();
        await Ingest(admin, asset, TestImages.Checkerboard(400), "v2.jpg");
        var v2 = await Thumb();
        Assert.NotEqual(v1.Headers.ETag!.Tag, v2.Headers.ETag!.Tag);
        Assert.NotEqual(v1.Headers.GetValues("X-Dam-Version").Single(), v2.Headers.GetValues("X-Dam-Version").Single());
    }

    // ---- transforms ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_transform_link_renders_exactly_the_requested_crop_and_format_and_caches_it()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var link = await Link(admin, asset, new { transform = new { w = 300, h = 200, fit = "cover", fmt = "jpg", q = 70, focal = "0.2,0.5" } });
        var url = PathOf(link.GetProperty("links").GetProperty("transform").GetString()!);
        Assert.Contains("/t?", url);

        var http = await Delivery();
        var res = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType!.MediaType);
        using (var img = Decode(await res.Content.ReadAsByteArrayAsync())) Assert.Equal((300, 200), (img.Width, img.Height));
        Assert.Equal("miss", res.Headers.GetValues("X-Dam-Cache").Single());
        Assert.Equal("hit", (await http.GetAsync(url)).Headers.GetValues("X-Dam-Cache").Single());
    }

    [Theory]
    [InlineData("w=301")]                          // changed a value
    [InlineData("w=300&h=200&fit=cover&fmt=jpg&q=70&focal=0.2,0.5&extra=1")]   // not exactly what was signed
    [InlineData("w=300&h=200&fit=cover&fmt=jpg&q=71&focal=0.2,0.5")]
    [InlineData("w=300&h=200&fit=fill&fmt=jpg&q=70&focal=0.2,0.5")]
    public async Task Tampering_with_transform_parameters_is_refused(string tampered)
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var url = PathOf((await Link(admin, asset, new { transform = new { w = 300, h = 200, fit = "cover", fmt = "jpg", q = 70, focal = "0.2,0.5" } })).GetProperty("links").GetProperty("transform").GetString()!);
        var tamperedUrl = url[..url.IndexOf('?')] + "?" + tampered;
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Delivery()).GetAsync(tamperedUrl)).StatusCode);
    }

    [Fact]
    public async Task A_transform_link_cannot_be_used_for_a_rendition_and_a_rendition_link_cannot_be_used_for_a_transform()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var http = await Delivery();
        var tLink = PathOf((await Link(admin, asset, new { transform = new { w = 100 } })).GetProperty("links").GetProperty("transform").GetString()!);
        var token = tLink.Split('/')[2];
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{token}/{asset}/thumb")).StatusCode);

        var rToken = PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" } })).GetProperty("links").GetProperty("thumb").GetString()!).Split('/')[2];
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{rToken}/{asset}/t?w=100&fit=cover&fmt=webp&q=80")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{rToken}/{asset}/web")).StatusCode);       // not in the signed list
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{rToken}/{asset}/original")).StatusCode);
    }

    // ---- the token ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tampered_or_foreign_token_or_one_for_another_asset_is_refused()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var other = await IngestedImage(admin, folder);
        var http = await Delivery();
        var url = PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" } })).GetProperty("links").GetProperty("thumb").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url)).StatusCode);

        var parts = url.Split('/');                               // "", d, token, asset, thumb
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{parts[2][..^3]}xyz/{asset}/thumb")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/not-a-token/{asset}/thumb")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{parts[2]}/{other}/thumb")).StatusCode);       // a different asset
        var forged = new DeliveryTokenSigner(["some-other-signing-key-0123456789abcdef"]).Sign(new DeliveryClaims(t, asset, "r:thumb", DateTimeOffset.UtcNow.AddHours(1), null, false));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/d/{forged}/{asset}/thumb")).StatusCode);
    }

    [Fact]
    public async Task A_link_expires_and_then_answers_410()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var url = PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" }, ttlSeconds = 2 })).GetProperty("links").GetProperty("thumb").GetString()!);
        var http = await Delivery();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_link_bound_to_an_address_only_works_from_that_address()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder);
        var http = await Delivery();
        var elsewhere = PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" }, ip = "203.0.113.9" })).GetProperty("links").GetProperty("thumb").GetString()!);
        var here = PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" }, ip = "127.0.0.1" })).GetProperty("links").GetProperty("thumb").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(here)).StatusCode);
    }

    [Fact]
    public async Task A_token_for_one_tenant_cannot_reach_another_tenants_asset()
    {
        var (_, adminA, folderA) = await TenantWithPacks();
        var (tenantB, _, _) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var assetA = await IngestedImage(adminA, folderA);
        var signer = new DeliveryTokenSigner([ApiTestHost.SigningKey]);
        var asB = signer.Sign(new DeliveryClaims(tenantB, assetA, "r:thumb", DateTimeOffset.UtcNow.AddHours(1), null, false));
        Assert.Equal(HttpStatusCode.NotFound, (await (await Delivery()).GetAsync($"/d/{asB}/{assetA}/thumb")).StatusCode);   // row-level security hides it
    }

    // ---- unpublished content is watermarked ----------------------------------------------------------------------

    [Fact]
    public async Task An_unpublished_asset_is_only_ever_served_watermarked_and_not_cached_publicly()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var asset = await IngestedImage(admin, folder, publish: false);
        var http = await Delivery();

        var draftLink = await Link(admin, asset, new { renditions = new[] { "preview" } });
        Assert.True(draftLink.GetProperty("watermarked").GetBoolean());
        var draftRes = await http.GetAsync(PathOf(draftLink.GetProperty("links").GetProperty("preview").GetString()!));
        Assert.Contains("private", draftRes.Headers.CacheControl!.ToString());
        var marked = await draftRes.Content.ReadAsByteArrayAsync();

        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        var pubLink = await Link(admin, asset, new { renditions = new[] { "preview" } });
        Assert.False(pubLink.GetProperty("watermarked").GetBoolean());
        var pubRes = await http.GetAsync(PathOf(pubLink.GetProperty("links").GetProperty("preview").GetString()!));
        Assert.Contains("public", pubRes.Headers.CacheControl!.ToString());
        var clean = await pubRes.Content.ReadAsByteArrayAsync();

        using var a = Decode(marked);
        using var b = Decode(clean);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        using var diff = (a.Flatten().Colourspace(Enums.Interpretation.Bw) - b.Flatten().Colourspace(Enums.Interpretation.Bw)).Abs();
        Assert.True(diff.Avg() > 0.5, "the draft copy should carry a watermark the published copy does not");

        // withdrawing a published asset makes even an old clean link start showing watermarks
        var cleanUrl = PathOf((await Link(admin, asset, new { renditions = new[] { "thumb" } })).GetProperty("links").GetProperty("thumb").GetString()!);
        Sql($"UPDATE assets SET status = 'draft' WHERE id = '{asset}'");
        Assert.Contains("private", (await http.GetAsync(cleanUrl)).Headers.CacheControl!.ToString());
    }

    // ---- originals ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_original_is_served_byte_for_byte_only_to_someone_cleared_for_it_and_only_once_published()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var file = NewPicture(640, 480);
        var asset = await IngestedImage(admin, folder, file, publish: false);

        var draft = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", admin, new { renditions = new[] { "original" } });
        Assert.Equal(HttpStatusCode.Conflict, draft.Status);                                  // never a clean copy of unreleased content

        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        var viewer = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", As(t, "Viewer"), new { renditions = new[] { "original" } });
        Assert.Equal(HttpStatusCode.Forbidden, viewer.Status);

        var link = await Link(admin, asset, new { renditions = new[] { "original" } });
        var res = await (await Delivery()).GetAsync(PathOf(link.GetProperty("links").GetProperty("original").GetString()!));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(file, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType!.MediaType);
    }

    // ---- what is refused ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Bad_link_requests_are_rejected_with_field_errors_and_hidden_assets_look_absent()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var asset = await NewAsset(admin, folder);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/links", admin, new { })).Status);   // no file yet

        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var real = await IngestedImage(admin, folder);
        async Task<(HttpStatusCode Status, JsonElement Body)> Post(object body) => await Call(HttpMethod.Post, $"/api/v1/assets/{real}/links", admin, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { renditions = new[] { "huge" } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { ttlSeconds = 0 })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { ttlSeconds = 999999 })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { ip = "not-an-ip" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { transform = new { fit = "cover" } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { transform = new { w = 99999 } })).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{real}/links", As(t, "Nobody"), new { })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{Guid.NewGuid()}/links", admin, new { })).Status);
    }

    [Fact]
    public async Task Files_of_a_kind_with_no_renditions_have_none_and_a_blocked_or_deleted_asset_is_not_served()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(NoScan); await worker.StartAsync();
        var doc = await NewAsset(admin, folder, type: "document");
        var u = await Ingest(admin, doc, "a,b\n1,2\n"u8.ToArray(), "data.csv", "text/csv");
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{doc}'");
        var http = await Delivery();
        var res = await http.GetAsync(PathOf((await Link(admin, doc, new { renditions = new[] { "thumb" } })).GetProperty("links").GetProperty("thumb").GetString()!));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("rendition_not_available", await res.Content.ReadAsStringAsync());

        var img = await IngestedImage(admin, folder);
        var url = PathOf((await Link(admin, img, new { renditions = new[] { "thumb" }, ttlSeconds = 600 })).GetProperty("links").GetProperty("thumb").GetString()!);
        Sql($"UPDATE assets SET status = 'expired' WHERE id = '{img}'");
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync(url)).StatusCode);
        Sql($"UPDATE assets SET status = 'deleted' WHERE id = '{img}'");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url)).StatusCode);
    }
}
