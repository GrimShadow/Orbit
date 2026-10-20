using System.Net;
using System.Text.Json;
using Dam.Api.Auth;
using Dam.Domain.Ai;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Net;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Dam.IntegrationTests;

/// <summary>
/// Semantic search over a real OpenSearch (ADR-0035), with deterministic embeddings: the vectors are written straight into <c>asset_embeddings</c> (as the
/// worker would), the assets indexed, and the query embedded by a fake sidecar whose "meaning" of words matches <see cref="FakeSpace"/>.
/// </summary>
[Collection("pg")]
public sealed class AiSearchTests(PostgresFixture pg, SearchFixture os) : AssetTestBase(pg), IClassFixture<SearchFixture>
{
    private FakeSidecar _sidecar = null!;
    protected override string? OpenSearchUrl => os.Url;

    protected override IReadOnlyDictionary<string, string?>? ApiEnvironment => new Dictionary<string, string?>
    {
        ["DAM_AI_PROVIDER"] = "self-hosted",
        ["DAM_AI_SIDECAR_URL"] = _sidecar.Url,
        ["DAM_AI_RETRY_BASE_MS"] = "1",
        ["DAM_AI_FAILURE_THRESHOLD"] = "1000",
    };

    public override async Task InitializeAsync()
    {
        _sidecar = await FakeSidecar.StartAsync();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _sidecar.DisposeAsync();
    }

    private static OpenSearchLowLevelClient Os(SearchFixture f) => new(new ConnectionConfiguration(new Uri(f.Url)));

    private async Task EnableAi(string admin, object? features = null)
    {
        var r = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, features });
        Assert.Equal(HttpStatusCode.OK, r.Status);
    }

    private async Task<Guid> Asset(string admin, string title, float[]? image = null, float[]? text = null, string type = "image", Guid? folder = null, string[]? tags = null, bool publish = false)
    {
        var id = await NewAsset(admin, folder, type, title, tags: tags);
        var version = Guid.NewGuid();
        Sql($"UPDATE assets SET current_version_id = '{version}' WHERE id = '{id}'");
        var tenant = Guid.Parse(Sql1<string>($"SELECT tenant_id::text FROM assets WHERE id = '{id}'"));
        await using (var db = Pg.AppContext(new TestTenant(tenant)))
        {
            if (image is not null) db.AssetEmbeddings.Add(AssetEmbedding.Create(tenant, id, version, EmbeddingKinds.Image, "test", image, DateTimeOffset.UtcNow));
            if (text is not null) db.AssetEmbeddings.Add(AssetEmbedding.Create(tenant, id, version, EmbeddingKinds.Text, "test", text, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        if (publish)
            foreach (var step in new[] { "submit", "approve", "publish" })
                Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{id}/transitions/{step}", admin, new { })).Status);
        return id;
    }

    private T Sql1<T>(string sql)
    {
        using var c = new Npgsql.NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return (T)cmd.ExecuteScalar()!;
    }

    private async Task Index(Guid tenant, params Guid[] ids)
    {
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantOverride>().Value = tenant;
            var writer = scope.ServiceProvider.GetRequiredService<ISearchIndexWriter>();
            foreach (var id in ids) await writer.IndexAsync(id, default);
        }
        await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.POST, $"/{IndexNaming.Alias("t-" + tenant.ToString("N")[..12])}/_refresh", default);
    }

    private async Task<JsonElement> Search(string token, object body)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/search", token, body);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body;
    }

    private static List<Guid> Ids(JsonElement result) => result.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
    private static float[] Img(params string[] concepts) => FakeSpace.Concept(512, concepts);

    // ---- the headline: words that appear nowhere still find the picture ---------------------------------------------

    [Fact]
    public async Task A_natural_language_query_finds_the_picture_whose_vector_matches_though_no_tag_or_text_does()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        var redSuvOnBeach = await Asset(admin, "IMG_0042", Img("red", "suv", "beach"), folder: folder, tags: ["outdoor", "travel"]);
        var mountain = await Asset(admin, "IMG_0043", Img("mountain"), folder: folder, tags: ["landscape"]);
        var office = await Asset(admin, "IMG_0044", Img("office"), folder: folder);
        var dog = await Asset(admin, "IMG_0045", Img("dog"), folder: folder);
        await Index(t, redSuvOnBeach, mountain, office, dog);

        var lexical = await Search(admin, new { query = "red SUV on beach" });
        Assert.Equal(0, lexical.GetProperty("total").GetInt64());                              // no tag, title or text says so
        Assert.False(lexical.GetProperty("semantic").GetBoolean());

        var semantic = await Search(admin, new { query = "red SUV on beach", semantic = true });
        Assert.True(semantic.GetProperty("semantic").GetBoolean());
        Assert.Equal([redSuvOnBeach], Ids(semantic));                                           // the others are not close enough to count
        Assert.Equal(["image"], Strings(semantic.GetProperty("hits")[0].GetProperty("matchedBy")));
        Assert.Null(semantic.GetProperty("semanticFallback").GetString());
        Assert.Contains("image", Strings(semantic.GetProperty("semanticLegs")));
        Assert.Equal(1, semantic.GetProperty("total").GetInt64());
        Assert.True(semantic.GetProperty("hits")[0].GetProperty("score").GetDouble() > 0);

        Assert.Equal([mountain], Ids(await Search(admin, new { query = "snowy alpine peak", semantic = true })));
        Assert.Empty(Ids(await Search(admin, new { query = "something nobody has a picture of", semantic = true })));
    }

    [Fact]
    public async Task A_saved_semantic_search_replays_as_a_hybrid_search_and_a_legacy_one_stays_a_text_search()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        var redSuv = await Asset(admin, "IMG_0142", Img("red", "suv", "beach"), folder: folder);
        var office = await Asset(admin, "IMG_0143", Img("office"), folder: folder);
        await Index(t, redSuv, office);

        async Task<Guid> Save(string name, object query) => Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name, type = "smart", query })).Body, "id");
        async Task<JsonElement> Run(Guid id)
        {
            var r = await Call(HttpMethod.Get, $"/api/v1/collections/{id}/assets", admin);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            return r.Body;
        }

        var meaning = await Run(await Save("By meaning", new { query = "red SUV on beach", semantic = true }));
        Assert.True(meaning.GetProperty("semantic").GetBoolean());
        Assert.Equal([redSuv], Ids(meaning));

        var words = await Run(await Save("By words", new { query = "red SUV on beach" }));
        Assert.False(words.GetProperty("semantic").GetBoolean());
        Assert.Empty(Ids(words));

        var legacyId = await Save("Legacy", new { query = "red SUV on beach", semantic = true });
        Sql($"UPDATE collections SET query = '{{\"Text\": \"red SUV on beach\", \"Highlight\": false}}'::jsonb WHERE id = '{legacyId}'");   // saved before the flag existed
        var legacy = await Run(legacyId);
        Assert.False(legacy.GetProperty("semantic").GetBoolean());
        Assert.Empty(Ids(legacy));
    }

    [Fact]
    public async Task The_meaning_of_a_assets_words_is_a_leg_of_its_own()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        var kitchen = await Asset(admin, "Recipe shoot", text: FakeSpace.Concept(384, "kitchen"), folder: folder);
        var other = await Asset(admin, "Boardroom", text: FakeSpace.Concept(384, "office"), folder: folder);
        await Index(t, kitchen, other);

        var r = await Search(admin, new { query = "chef cooking", semantic = true });     // shares no word with either title
        Assert.Equal([kitchen], Ids(r));
        Assert.Equal(["text"], Strings(r.GetProperty("hits")[0].GetProperty("matchedBy")));
        Assert.Contains("meaning", Strings(r.GetProperty("semanticLegs")));
    }

    [Fact]
    public async Task Fusing_the_legs_puts_the_item_good_in_both_ahead_of_the_best_of_either()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        // lexical order: A (every word), C (one word), B (none). image order: B (exact), C (close), A (unrelated, so below the floor).
        var a = await Asset(admin, "Red SUV on the beach poster", Img("dog"), folder: folder);
        var b = await Asset(admin, "IMG_9001", Img("red", "suv", "beach"), folder: folder);
        var closeToQuery = Img("red", "suv", "beach"); closeToQuery[FakeSpace.Axis("dog")] = 0.25f;
        var c = await Asset(admin, "Beach holiday", FakeSpace.Normalize(closeToQuery), folder: folder);
        await Index(t, a, b, c);

        var lexical = await Search(admin, new { query = "red SUV on beach" });
        Assert.Equal([a, c], Ids(lexical));                                                   // A beats C by words alone, B is missing
        var hybrid = await Search(admin, new { query = "red SUV on beach", semantic = true });
        var order = Ids(hybrid);
        Assert.Equal(c, order[0]);                                                            // second in both lists beats first in one
        Assert.Equal(new[] { a, b }.OrderBy(x => x), order.Skip(1).OrderBy(x => x));          // the single-leg winners follow, tied
        var hits = hybrid.GetProperty("hits").EnumerateArray().ToDictionary(h => h.GetProperty("id").GetGuid());
        Assert.Equal(["text", "image"], Strings(hits[c].GetProperty("matchedBy")));
        Assert.Equal(["text"], Strings(hits[a].GetProperty("matchedBy")));
        Assert.Equal(["image"], Strings(hits[b].GetProperty("matchedBy")));
        Assert.True(hits[c].GetProperty("score").GetDouble() > hits[a].GetProperty("score").GetDouble());
        Assert.Equal(3, hybrid.GetProperty("total").GetInt64());
    }

    // ---- access control is part of every leg ------------------------------------------------------------------------

    [Fact]
    public async Task An_asset_the_caller_may_not_read_never_appears_through_the_vector_leg()
    {
        var (t, admin, brandFolder) = await TenantWithPacks();
        await EnableAi(admin);
        var south = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "South", inheritedMetadata = new { regions = new[] { "south" } } })).Body, "folder", "id");
        var northAsset = await Asset(admin, "IMG_N", Img("red", "suv", "beach"), folder: brandFolder, publish: true);   // inherits region north
        var southAsset = await Asset(admin, "IMG_S", Img("red", "suv", "beach"), folder: south, publish: true);          // a perfect match, but for another region
        var draft = await Asset(admin, "IMG_D", Img("red", "suv", "beach"), folder: brandFolder);                         // an unpublished draft
        await Index(t, northAsset, southAsset, draft);

        var dealer = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Dealer"], attributes: new() { ["region"] = ["north"] });
        var asDealer = await Search(dealer, new { query = "red suv beach", semantic = true });
        Assert.Equal([northAsset], Ids(asDealer));
        Assert.Equal(1, asDealer.GetProperty("total").GetInt64());

        var asAdmin = await Search(admin, new { query = "red suv beach", semantic = true });
        Assert.Equal(new[] { northAsset, southAsset, draft }.OrderBy(x => x), Ids(asAdmin).OrderBy(x => x));               // they do match: only access kept them out

        var viewer = As(t, "Viewer");                                                                                        // published only, any region
        Assert.Equal(new[] { northAsset, southAsset }.OrderBy(x => x), Ids(await Search(viewer, new { query = "red suv beach", semantic = true })).OrderBy(x => x));

        // user filters are applied inside the legs as well, and facets count the fused set
        var filtered = await Search(admin, new { query = "red suv beach", semantic = true, filters = new Dictionary<string, string[]> { ["status"] = ["published"] }, facets = new[] { "status" } });
        Assert.Equal(new[] { northAsset, southAsset }.OrderBy(x => x), Ids(filtered).OrderBy(x => x));
        var facet = filtered.GetProperty("facets").EnumerateArray().Single().GetProperty("values").EnumerateArray().Single();
        Assert.Equal(("published", 2), (facet.GetProperty("value").GetString(), (int)facet.GetProperty("count").GetInt64()));
    }

    // ---- degrading gracefully -----------------------------------------------------------------------------------------

    [Fact]
    public async Task When_ai_is_off_or_fails_a_semantic_search_runs_as_a_text_search_and_says_why()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var a = await Asset(admin, "Roadster hero shot", Img("red", "suv", "beach"), folder: folder);
        await Index(t, a);

        var off = await Search(admin, new { query = "roadster", semantic = true });
        Assert.Equal([a], Ids(off));                                                          // the words still find it
        Assert.False(off.GetProperty("semantic").GetBoolean());
        Assert.Equal("ai_disabled", off.GetProperty("semanticFallback").GetString());
        Assert.Equal(0, _sidecar.Calls.Count(c => c.Path != "/healthz"));                   // nothing was sent while the tenant has AI off

        await EnableAi(admin, new { tags = true, ocr = true, transcribe = true, embeddings = false });
        Assert.Equal("embeddings_disabled", (await Search(admin, new { query = "roadster", semantic = true })).GetProperty("semanticFallback").GetString());

        await EnableAi(admin);
        _sidecar.Down = true;
        var down = await Search(admin, new { query = "roadster", semantic = true });
        Assert.Equal("provider_unavailable", down.GetProperty("semanticFallback").GetString());
        Assert.Equal([a], Ids(down));
        _sidecar.Down = false;

        Assert.Equal("empty_query", (await Search(admin, new { semantic = true })).GetProperty("semanticFallback").GetString());
        Assert.Equal("similar_to", (await Search(admin, new { semantic = true, similarTo = a, query = "x" })).GetProperty("semanticFallback").GetString());

        var ok = await Search(admin, new { query = "roadster", semantic = true });
        Assert.True(ok.GetProperty("semantic").GetBoolean());
        Assert.Null(ok.GetProperty("semanticFallback").GetString());
    }

    [Fact]
    public async Task A_provider_without_image_embeddings_still_searches_by_the_meaning_of_the_words()
    {
        // The OpenAI-compatible provider has no image vectors; semantic search then uses the text leg only (ADR-0035).
        await using var openAi = await FakeOpenAi.StartAsync();
        var env = new Dictionary<string, string?>
        {
            ["DAM_AI_PROVIDER"] = "openai-compatible",
            ["DAM_AI_BASE_URL"] = openAi.Url + "/v1",
            ["DAM_AI_API_KEY"] = openAi.ExpectedKey,
        };
        using var api = new ApiTestHost(Pg).Factory(openSearchUrl: os.Url, extraEnvironment: env);
        var (t, admin, folder) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true })).Status);   // the API under test is the default one; settings are shared state
        var kitchen = await Asset(admin, "Recipe shoot", text: FakeSpace.Concept(384, "kitchen"), folder: folder);
        await Index(t, kitchen);

        using var client = api.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/search") { Content = System.Net.Http.Json.JsonContent.Create(new { query = "chef cooking", semantic = true }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
        using var res = await client.SendAsync(req);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal([kitchen], Ids(body));
        Assert.Equal(["text", "meaning"], Strings(body.GetProperty("semanticLegs")));          // no picture leg: the provider has no image vectors
        Assert.Equal(1, openAi.CallsTo("/embeddings"));      // one call for the query: no CLIP leg
    }

    // ---- paging -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_semantic_search_pages_by_offset_through_the_fused_list_and_ignores_the_sort()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var v = Img("red", "suv", "beach");
            v[FakeSpace.Axis("dog")] = 0.02f * i;   // all close, in a known order of closeness
            ids.Add(await Asset(admin, $"Photo {5 - i}", FakeSpace.Normalize(v), folder: folder));
        }
        await Index(t, [.. ids]);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Search(admin, new { query = "red suv beach", semantic = true, limit = 2, cursor, sort = "title" });   // title order would reverse it
            Assert.Equal(5, page.GetProperty("total").GetInt64());
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(3, pages);
        Assert.Equal(ids, seen);                                                              // closest first, each exactly once, sort ignored
        Assert.Equal(ids, Ids(await Search(admin, new { query = "red suv beach", semantic = true, limit = 50, sort = "-title" })));
    }

    // ---- the index ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_asset_without_embeddings_is_indexed_as_before_and_a_reindex_restores_vectors_from_the_database()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        var plain = await Asset(admin, "Plain asset", folder: folder);
        var withVectors = await Asset(admin, "IMG_7", Img("mountain"), FakeSpace.Concept(384, "mountain"), folder: folder);
        await Index(t, plain, withVectors);
        var alias = IndexNaming.Alias("t-" + t.ToString("N")[..12]);

        var plainDoc = JsonDocument.Parse((await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{plain}", default)).Body).RootElement.GetProperty("_source");
        Assert.False(plainDoc.TryGetProperty("vector_image", out _));
        var vecDoc = JsonDocument.Parse((await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{withVectors}", default)).Body).RootElement.GetProperty("_source");
        Assert.Equal(512, vecDoc.GetProperty("vector_image").GetArrayLength());
        Assert.Equal(384, vecDoc.GetProperty("vector_text").GetArrayLength());

        // Rebuild the whole index: the vectors come back from asset_embeddings, with no provider involved.
        var before = _sidecar.Calls.Count(c => c.Path != "/healthz");
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantOverride>().Value = t;
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<ISearchIndexAdmin>().ReindexAsync(default));
        }
        await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.POST, $"/{alias}/_refresh", default);
        Assert.Equal(before, _sidecar.Calls.Count(c => c.Path != "/healthz"));                // the rebuild asked the provider for nothing
        Assert.Equal([withVectors], Ids(await Search(admin, new { query = "snow mountain", semantic = true })));
    }

    [Fact]
    public async Task Vectors_of_the_wrong_size_are_left_out_of_the_index_and_an_index_built_for_other_sizes_asks_for_a_reindex()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        var wrongSize = await Asset(admin, "Wrong size", new float[100], folder: folder);          // 100 dimensions, 512 configured
        var good = await Asset(admin, "IMG_8", Img("office"), folder: folder);

        // An index made for other sizes (the placeholder sizes before this step, or after DAM_AI_*_DIM changed): 1024 text and 256 image dimensions.
        var alias = IndexNaming.Alias("t-" + t.ToString("N")[..12]);
        var oldIndex = alias + "-old";
        var body = IndexMappingBuilder.BuildIndexBody([], new VectorDimensions(1024, 256));
        Assert.True((await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.PUT, $"/{oldIndex}", default, PostData.String(JsonSerializer.Serialize(body)))).Success);
        Assert.True((await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.POST, "/_aliases", default,
            PostData.String(JsonSerializer.Serialize(new { actions = new object[] { new { add = new { index = oldIndex, alias } } } })))).Success);

        // Indexing still works: the text goes in, the vectors wait for a reindex.
        await Index(t, wrongSize, good);
        var status = (await Call(HttpMethod.Get, "/api/v1/ai/status", admin)).Body.GetProperty("vectorIndex");
        Assert.True(status.GetProperty("needsReindex").GetBoolean());
        Assert.Equal(1024, status.GetProperty("liveTextDim").GetInt32());
        var fallback = await Search(admin, new { query = "office desk", semantic = true });
        Assert.Equal("vectors_not_indexed", fallback.GetProperty("semanticFallback").GetString());

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantOverride>().Value = t;
            await scope.ServiceProvider.GetRequiredService<ISearchIndexAdmin>().ReindexAsync(default);
        }
        await Os(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.POST, $"/{alias}/_refresh", default);
        Assert.False((await Call(HttpMethod.Get, "/api/v1/ai/status", admin)).Body.GetProperty("vectorIndex").GetProperty("needsReindex").GetBoolean());
        Assert.Equal([good], Ids(await Search(admin, new { query = "office desk", semantic = true })));    // the wrong-sized one never made it in
    }

    // ---- extracted text and transcripts ---------------------------------------------------------------------------------

    [Fact]
    public async Task Text_read_from_pictures_and_spoken_words_are_searchable_and_a_spoken_match_says_when()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var scan = await Asset(admin, "Scanned flyer", folder: folder, type: "document");
        var clip = await Asset(admin, "Showroom walkthrough", folder: folder, type: "video");
        var plain = await Asset(admin, "Nothing to find here", folder: folder);
        var scanVersion = Guid.NewGuid(); var clipVersion = Guid.NewGuid();
        Sql($"UPDATE assets SET current_version_id = '{scanVersion}' WHERE id = '{scan}'");
        Sql($"UPDATE assets SET current_version_id = '{clipVersion}' WHERE id = '{clip}'");
        await using (var db = Pg.AppContext(new TestTenant(t)))
        {
            var now = DateTimeOffset.UtcNow;
            var ocr = AssetExtract.Create(t, scan, scanVersion, now);
            ocr.SetOcr("Opening hours: weekdays nine to five, closed on public holidays", "en", "ocr-test", now);
            var speech = AssetExtract.Create(t, clip, clipVersion, now);
            speech.SetTranscript(
            [
                new TranscriptSegment(0, 2500, "Welcome to the Acme showroom"),
                new TranscriptSegment(2500, 6000, "Our new roadster has a panoramic roof"),
                new TranscriptSegment(6000, 9000, "The panoramic roof opens at the touch of a button"),
                new TranscriptSegment(9000, 12000, "Another panoramic view from the back seat"),
                new TranscriptSegment(12000, 15000, "A last panoramic remark"),
            ], "en", 15000, "whisper-test", now);
            db.AssetExtracts.AddRange(ocr, speech);
            await db.SaveChangesAsync();
        }
        await Index(t, scan, clip, plain);

        var ocrHit = await Search(admin, new { query = "public holidays" });
        Assert.Equal([scan], Ids(ocrHit));
        Assert.Equal(["ocr"], Strings(ocrHit.GetProperty("hits")[0].GetProperty("matchedBy")));
        Assert.False(ocrHit.GetProperty("hits")[0].TryGetProperty("transcriptMatches", out var none) && none.ValueKind == JsonValueKind.Array);

        var spoken = await Search(admin, new { query = "panoramic roof", highlight = true });
        Assert.Equal([clip], Ids(spoken));
        var hit = spoken.GetProperty("hits")[0];
        Assert.Equal(["transcript"], Strings(hit.GetProperty("matchedBy")));
        var matches = hit.GetProperty("transcriptMatches").EnumerateArray().ToList();
        Assert.Equal(3, matches.Count);                                                       // at most three, in time order
        Assert.Equal(matches.Select(m => m.GetProperty("startMs").GetInt64()).Order(), matches.Select(m => m.GetProperty("startMs").GetInt64()));
        Assert.Contains(matches, m => m.GetProperty("startMs").GetInt64() == 2500 && m.GetProperty("text").GetString() == "Our new roadster has a panoramic roof");
        Assert.True(hit.GetProperty("highlights").TryGetProperty("transcript_text", out _));

        // the same words, found by a semantic search, still say when
        await EnableAi(admin);
        var viaHybrid = await Search(admin, new { query = "panoramic roof", semantic = true });
        Assert.Equal(clip, Ids(viaHybrid)[0]);
        Assert.NotEmpty(viaHybrid.GetProperty("hits")[0].GetProperty("transcriptMatches").EnumerateArray());

        // a plain browse request carries neither
        var browse = await Search(admin, new { });
        Assert.All(browse.GetProperty("hits").EnumerateArray(), h => Assert.False(h.TryGetProperty("matchedBy", out var m) && m.ValueKind == JsonValueKind.Array));
    }
}
