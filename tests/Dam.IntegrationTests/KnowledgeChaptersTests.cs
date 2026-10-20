using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Dam.Infrastructure.Knowledge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static Dam.UnitTests.Support.TestImages;

namespace Dam.IntegrationTests;

/// <summary>
/// Chapter indexing by the worker (ADR-0039 decision 7) and the way a new version replaces a manual silently (decision 6): real files go through the real ingest pipeline,
/// then the <c>knowledge-chapters</c> consumer reads them. Every test waits on state, never on a timer.
/// </summary>
[Collection("pg")]
public sealed class KnowledgeChaptersTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private const string Vin = "ACMRS1AB4RA000123";

    private static readonly OutlineEntry[] Outline =
    [
        new("Welcome", 1, 1), new("Safety", 1, 2), new("Seat belts", 2, 3), new("Airbags", 2, 5), new("Driving", 1, 7),
    ];

    private IHost Worker()
    {
        var w = StartWorker(new Dictionary<string, string?> { ["DAM_SCAN_MODE"] = "off" }, renditions: false, knowledge: true);
        return w;
    }

    private async Task<(Guid Tenant, string Admin)> World()
    {
        var t = await NewTenantAsync("core", "automotive-sample");
        return (t, Admin(t));
    }

    private async Task<Guid> Manual(string admin, string type = "manual", string title = "Roadster owner's manual") =>
        await NewAsset(admin, null, type, title, Meta(), allowIncomplete: true);

    private async Task<Guid> MapDoc(string admin, Guid asset, string model = "roadster")
    {
        var r = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = asset, docType = "owner_manual", model, language = "en" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Body.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> WaitDoc(string admin, Guid doc, Func<JsonElement, bool> ready, int seconds = 45)
    {
        JsonElement last = default;
        for (var i = 0; i < seconds * 10; i++)
        {
            last = (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body;
            if (last.ValueKind == JsonValueKind.Object && ready(last)) return last;
            await Task.Delay(100);
        }
        throw new TimeoutException("The document never reached the expected state; last seen: " + last);
    }

    private static string[] Titles(JsonElement doc) => doc.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToArray();

    private long Owner(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Waits until the consumer has handled <paramref name="n"/> messages for the tenant (recorded on commit), so "nothing changed" can be asserted afterwards.</summary>
    private async Task WaitHandled(Guid tenant, long n)
    {
        for (var i = 0; i < 450; i++)
        {
            if (Owner($"SELECT count(*) FROM processed_messages WHERE consumer = 'knowledge-chapters' AND tenant_id = '{tenant}'") >= n) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The consumer did not handle the messages.");
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        return ms.ToArray();
    }

    // ---- the three sources ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pdfs_bookmarks_become_chapters_with_levels_and_page_ranges_when_the_document_is_made_after_the_file()
    {
        var (t, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        Assert.Equal("ingested", (await Ingest(admin, asset, PdfWithOutline(9, Outline), "manual.pdf", "application/pdf")).GetProperty("status").GetString());

        var doc = await MapDoc(admin, asset);
        var d = await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");
        Assert.Equal(["Welcome", "Safety", "Seat belts", "Airbags", "Driving"], Titles(d));
        Assert.Equal([1, 1, 2, 2, 1], d.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("level").GetInt32()).ToArray());
        Assert.Equal([1, 2, 3, 5, 7], d.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("pageFrom").GetInt32()).ToArray());
        Assert.Equal([1, 6, 4, 6, 9], d.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("pageTo").GetInt32()).ToArray());
        Assert.Equal(5, d.GetProperty("chapterCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, d.GetProperty("chaptersIndexedAt").ValueKind);
        Assert.Equal("pdf", (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}/chapters", admin)).Body.GetProperty("source").GetString());
        Assert.True(Owner($"SELECT count(*) FROM audit_log WHERE tenant_id = '{t}' AND action = 'knowledge.chapters.indexed'") >= 1);
    }

    [Fact]
    public async Task A_document_made_before_the_file_is_indexed_when_the_version_arrives()
    {
        var (_, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        var doc = await MapDoc(admin, asset);                                         // a shell: nothing to read yet
        Assert.Equal("none", (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body.GetProperty("chaptersSource").GetString());

        await Ingest(admin, asset, PdfWithOutline(9, Outline), "manual.pdf", "application/pdf");
        var d = await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");
        Assert.Equal(5, d.GetProperty("chapterCount").GetInt32());
    }

    [Fact]
    public async Task The_headings_of_an_html_manual_become_chapters_with_anchors()
    {
        var (_, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        var html = "<!doctype html><html><head><title>Manual</title></head><body><h1 id=\"intro\">Introduction</h1><h2>Charging &amp; range</h2><h2 id=\"care\">Care</h2><h4>ignored</h4></body></html>";
        Assert.Equal("ingested", (await Ingest(admin, asset, Encoding.UTF8.GetBytes(html), "manual.html", "text/html")).GetProperty("status").GetString());
        var doc = await MapDoc(admin, asset);

        var d = await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "html");
        Assert.Equal(["Introduction", "Charging & range", "Care"], Titles(d));
        Assert.Equal(["intro", "charging-range", "care"], d.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("anchor").GetString()!).ToArray());
        Assert.Equal([1, 2, 2], d.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("level").GetInt32()).ToArray());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("chapters")[0].GetProperty("pageFrom").ValueKind);
    }

    [Fact]
    public async Task The_entry_page_of_an_html5_bundle_is_read_and_a_hostile_bundle_gives_no_chapters_and_no_harm()
    {
        var (_, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();

        var good = await Manual(admin, "html5", "Interactive manual");
        var bundle = Zip(("assets/app.js", "alert('never run')"), ("index.html", "<html><body><h1 id=\"top\">Interactive manual</h1><h2>Start here</h2><script src=\"http://example.invalid/x.js\"></script></body></html>"));
        Assert.Equal("ingested", (await Ingest(admin, good, bundle, "bundle.zip", "application/zip")).GetProperty("status").GetString());
        var goodDoc = await MapDoc(admin, good);

        var evil = await Manual(admin, "html5", "Hostile bundle");
        var hostile = Zip(("../evil/index.html", "<h1>Should never be read</h1>"), ("/abs/page.html", "<h1>Nor this</h1>"));
        Assert.Equal("ingested", (await Ingest(admin, evil, hostile, "evil.zip", "application/zip")).GetProperty("status").GetString());
        var evilDoc = await MapDoc(admin, evil);

        var g = await WaitDoc(admin, goodDoc, x => x.GetProperty("chaptersSource").GetString() == "html");
        Assert.Equal(["Interactive manual", "Start here"], Titles(g));
        Assert.Equal("top", g.GetProperty("chapters")[0].GetProperty("anchor").GetString());

        await WaitDoc(admin, evilDoc, x => x.GetProperty("chaptersIndexedAt").ValueKind != JsonValueKind.Null);       // read, found nothing
        var e = (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{evilDoc}", admin)).Body;
        Assert.Equal("none", e.GetProperty("chaptersSource").GetString());
        Assert.Equal(0, e.GetProperty("chapterCount").GetInt32());
    }

    [Fact]
    public async Task A_pdf_with_no_bookmarks_a_broken_outline_or_a_huge_one_never_fails_the_document()
    {
        var (_, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();

        async Task<(Guid Doc, JsonElement Result)> Index(byte[] pdf, string name, Func<JsonElement, bool>? ready = null)
        {
            var asset = await Manual(admin, title: name);
            await Ingest(admin, asset, pdf, name + ".pdf", "application/pdf");
            var doc = await MapDoc(admin, asset, name);
            return (doc, await WaitDoc(admin, doc, ready ?? (x => x.GetProperty("chaptersIndexedAt").ValueKind != JsonValueKind.Null)));
        }

        var plain = await Index(Pdf("Plain", pages: 3), "plain");
        Assert.Equal("none", plain.Result.GetProperty("chaptersSource").GetString());

        var looping = await Index(PdfWithOutline(6, [new("One", 1, 1), new("Two", 1, 3), new("Three", 1, 5)], loop: true), "looping");
        Assert.Equal("pdf", looping.Result.GetProperty("chaptersSource").GetString());
        Assert.InRange(looping.Result.GetProperty("chapterCount").GetInt32(), 3, 500);

        var nowhere = await Index(PdfWithOutline(8, [new("Part A", 1, null), new("A1", 2, 2), new("Named", 1, null, Named: true), new("Far", 1, 99), new("End", 1, 8)]), "nowhere");
        Assert.Equal(["Part A", "A1", "Named", "Far", "End"], Titles(nowhere.Result));
        Assert.Equal(2, nowhere.Result.GetProperty("chapters")[0].GetProperty("pageFrom").GetInt32());                  // the heading takes its first section's page
        Assert.Equal(JsonValueKind.Null, nowhere.Result.GetProperty("chapters")[2].GetProperty("pageFrom").ValueKind);

        var huge = await Index(PdfWithOutline(20, Enumerable.Range(1, 650).Select(i => new OutlineEntry($"Section {i}", 1, 1 + i % 20)).ToList()), "huge");
        Assert.Equal(500, huge.Result.GetProperty("chapterCount").GetInt32());

        var damaged = PdfWithOutline(4, Outline);
        var cut = await Index(damaged[..(damaged.Length / 2)], "damaged");                                               // cut in half: whatever can be read is kept, and the document is indexed all the same
        Assert.InRange(cut.Result.GetProperty("chapterCount").GetInt32(), 0, 5);
    }

    // ---- new versions replace silently ----------------------------------------------------------------------------

    [Fact]
    public async Task A_new_version_of_a_published_manual_replaces_it_silently_and_the_old_references_still_resolve()
    {
        var (t, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        await Ingest(admin, asset, PdfWithOutline(9, Outline), "manual-v1.pdf", "application/pdf");
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        var doc = await MapDoc(admin, asset);
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, new { name = "roadster", pattern = "ACM*", model = "roadster" })).Status);
        await WaitDoc(admin, doc, x => x.GetProperty("chapterCount").GetInt32() == 5);

        var before = await Call(HttpMethod.Get, "/api/v1/knowledge/resolve?vin=" + Vin, admin);
        Assert.Equal(HttpStatusCode.OK, before.Status);
        var v1 = before.Body.GetProperty("document");
        Assert.Equal(1, v1.GetProperty("versionNumber").GetInt32());
        var versionOne = v1.GetProperty("versionId").GetGuid();
        Assert.Equal(5, v1.GetProperty("chapters").GetArrayLength());

        // version 2 is a different file with a different table of contents
        var second = new[] { new OutlineEntry("Getting started", 1, 1), new("Charging", 1, 4), new("Fault finding", 1, 8) };
        Assert.Equal("ingested", (await Ingest(admin, asset, PdfWithOutline(10, second), "manual-v2.pdf", "application/pdf")).GetProperty("status").GetString());

        var d = await WaitDoc(admin, doc, x => x.GetProperty("chapterCount").GetInt32() == 3);
        Assert.Equal(["Getting started", "Charging", "Fault finding"], Titles(d));
        var after = await Call(HttpMethod.Get, "/api/v1/knowledge/resolve?vin=" + Vin, admin);
        Assert.Equal(HttpStatusCode.OK, after.Status);
        var v2 = after.Body.GetProperty("document");
        Assert.Equal(2, v2.GetProperty("versionNumber").GetInt32());
        Assert.NotEqual(versionOne, v2.GetProperty("versionId").GetGuid());
        Assert.Equal(v2.GetProperty("versionId").GetGuid(), v2.GetProperty("currentVersionId").GetGuid());
        Assert.Equal(["Getting started", "Charging", "Fault finding"], v2.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToArray());
        Assert.Equal(10, v2.GetProperty("chapters")[2].GetProperty("pageTo").GetInt32());

        // nothing that pointed at the manual had to change: same document, same asset, same published status
        Assert.Equal(doc, v2.GetProperty("id").GetGuid());
        Assert.Equal(asset, v2.GetProperty("assetId").GetGuid());
        Assert.Equal("published", v2.GetProperty("assetStatus").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Status);
        Assert.Equal(2, Owner($"SELECT count(*) FROM asset_versions WHERE asset_id = '{asset}'"));
        Assert.Equal(1, Owner($"SELECT count(*) FROM knowledge_documents WHERE tenant_id = '{t}'"));
    }

    [Fact]
    public async Task Manual_chapters_survive_a_new_version_and_a_plain_reindex_and_a_forced_one_replaces_them()
    {
        var (t, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        await Ingest(admin, asset, PdfWithOutline(9, Outline), "manual-v1.pdf", "application/pdf");
        var doc = await MapDoc(admin, asset);
        await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");

        var put = await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}/chapters", admin, new { chapters = new[] { new { title = "My own chapter", level = 1 } } });
        Assert.Equal(HttpStatusCode.OK, put.Status);

        var handled = Owner($"SELECT count(*) FROM processed_messages WHERE consumer = 'knowledge-chapters' AND tenant_id = '{t}'");
        await Ingest(admin, asset, PdfWithOutline(10, [new("From version two", 1, 1)]), "manual-v2.pdf", "application/pdf");
        await WaitHandled(t, handled + 1);                                                      // the version event was handled...
        var kept = (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body;
        Assert.Equal("manual", kept.GetProperty("chaptersSource").GetString());                  // ...and left the person's chapters alone
        Assert.Equal(["My own chapter"], Titles(kept));

        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{doc}/reindex", admin)).Status);
        await WaitHandled(t, handled + 2);
        Assert.Equal(["My own chapter"], Titles((await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body));

        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{doc}/reindex?force=true", admin)).Status);
        var replaced = await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");
        Assert.Equal(["From version two"], Titles(replaced));
    }

    [Fact]
    public async Task Clearing_manual_chapters_hands_the_document_back_to_the_worker_which_reads_the_file_again()
    {
        var (_, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        await Ingest(admin, asset, PdfWithOutline(9, Outline), "manual.pdf", "application/pdf");
        var doc = await MapDoc(admin, asset);
        await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");
        await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}/chapters", admin, new { chapters = new[] { new { title = "Mine", level = 1 } } });
        Assert.Equal("manual", (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body.GetProperty("chaptersSource").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}/chapters", admin, new { chapters = Array.Empty<object>() })).Status);
        var back = await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");
        Assert.Equal(5, back.GetProperty("chapterCount").GetInt32());
    }

    // ---- idempotence and the shape of the consumer -----------------------------------------------------------------

    [Fact]
    public async Task Indexing_the_same_version_twice_changes_nothing_and_announces_nothing()
    {
        var (t, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var asset = await Manual(admin);
        await Ingest(admin, asset, PdfWithOutline(9, Outline), "manual.pdf", "application/pdf");
        var doc = await MapDoc(admin, asset);
        var indexed = await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");
        var announcements = Owner($"SELECT count(*) FROM audit_log WHERE tenant_id = '{t}' AND action = 'knowledge.chapters.indexed'");
        var handled = Owner($"SELECT count(*) FROM processed_messages WHERE consumer = 'knowledge-chapters' AND tenant_id = '{t}'");

        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{doc}/reindex", admin)).Status);
        await WaitHandled(t, handled + 1);

        var again = (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body;
        Assert.Equal(indexed.GetProperty("chaptersIndexedAt").GetDateTimeOffset(), again.GetProperty("chaptersIndexedAt").GetDateTimeOffset());
        Assert.Equal(announcements, Owner($"SELECT count(*) FROM audit_log WHERE tenant_id = '{t}' AND action = 'knowledge.chapters.indexed'"));

        // forced: the file is read again and the result announced once more
        await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{doc}/reindex?force=true", admin);
        await WaitHandled(t, handled + 2);
        Assert.Equal(announcements + 1, Owner($"SELECT count(*) FROM audit_log WHERE tenant_id = '{t}' AND action = 'knowledge.chapters.indexed'"));
        Assert.Equal(Titles(indexed), Titles((await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", admin)).Body));
    }

    [Fact]
    public async Task A_new_version_of_an_asset_with_no_knowledge_document_is_handled_and_left_alone()
    {
        var (t, admin) = await World();
        using var worker = Worker(); await worker.StartAsync();
        var plain = await Manual(admin, title: "Not a knowledge document");
        await Ingest(admin, plain, PdfWithOutline(8, Outline), "plain.pdf", "application/pdf");
        var mapped = await Manual(admin);
        await Ingest(admin, mapped, PdfWithOutline(9, Outline), "mapped.pdf", "application/pdf");
        var doc = await MapDoc(admin, mapped);
        await WaitDoc(admin, doc, x => x.GetProperty("chaptersSource").GetString() == "pdf");

        await WaitHandled(t, 3);                                       // both version events and the document's own were handled, none was left to fail
        Assert.Equal(1, Owner($"SELECT count(*) FROM knowledge_documents WHERE tenant_id = '{t}'"));
        Assert.Equal(0, Owner($"SELECT count(*) FROM audit_log WHERE tenant_id = '{t}' AND action = 'knowledge.chapters.indexed' AND entity_id <> '{doc}'"));
    }

    [Fact]
    public async Task The_consumer_opts_out_of_the_host_transaction_so_parsing_never_holds_the_audit_chain_lock()
    {
        using var worker = Worker();
        using var scope = worker.Services.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<KnowledgeChaptersConsumer>();
        Assert.False(consumer.RunsInHostTransaction);
        Assert.Equal("knowledge-chapters", consumer.Name);
        Assert.Equal(["asset.version.added", "knowledge.document.created", "knowledge.document.reindex_requested"], consumer.RoutingKeys.Order());
        await Task.CompletedTask;
    }
}
