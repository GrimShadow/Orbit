using System.Net;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Dam.IntegrationTests;

/// <summary>
/// Translation, the glossary and the review queue end to end (ADR-0036): the API takes the request, the worker's <c>ai-translate</c> consumer protects the glossary
/// terms, asks a fake OpenAI-compatible service, checks what comes back and stores pending suggestions; a person accepts them into the asset's metadata. Real
/// Postgres, RabbitMQ and OpenSearch; the AI service is a fake whose translator is a literal one (the text in capitals) that tests can make misbehave.
/// </summary>
[Collection("pg")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in DisposeAsync (xUnit IAsyncLifetime).")]
public sealed class TranslationTests(PostgresFixture pg, RabbitFixture mq, SearchFixture os) : AiWorkerTestBase(pg, mq, os), IClassFixture<RabbitFixture>, IClassFixture<SearchFixture>
{
    private readonly RabbitFixture _mq = mq;
    private const string RoadsterText = "Meet the Acme Roadster X. A coastal convertible.";

    private async Task<(Guid T, string Admin, Guid Folder)> OpenAiTenant()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin, "openai-compatible");
        return (t, admin, folder);
    }

    private Task<Guid> Described(string admin, Guid folder, object description, string title = "Roadster hero") =>
        NewAsset(admin, folder, "image", title, metadata: new { description });

    private async Task<Guid> Term(string admin, string term, string mode = "locked", Dictionary<string, string>? translations = null)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/glossary", admin, new { term, mode, translations });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Body.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Translate(string token, Guid asset, object body)
    {
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", token, body);
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        return r.Body;
    }

    private async Task<JsonElement> WaitForJob(string token, Guid asset, Guid job)
    {
        JsonElement last = default;
        for (var i = 0; i < 300; i++)
        {
            var body = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/translations", token)).Body;
            last = body.GetProperty("jobs").EnumerateArray().FirstOrDefault(j => j.GetProperty("id").GetGuid() == job);
            if (last.ValueKind == JsonValueKind.Object && last.GetProperty("status").GetString() is "succeeded" or "partial" or "failed") return last;
            await Task.Delay(100);
        }
        throw new TimeoutException($"The translation job never finished; last answer: {last}");
    }

    private async Task<JsonElement> Run(string token, Guid asset, object body)
    {
        var job = await Translate(token, asset, body);
        return await WaitForJob(token, asset, job.GetProperty("id").GetGuid());
    }

    private async Task<List<JsonElement>> Suggestions(string token, Guid asset, string status = "pending") =>
        [.. (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", token)).Body.GetProperty("suggestions").EnumerateArray()
            .Where(s => s.GetProperty("kind").GetString() == "translation" && s.GetProperty("status").GetString() == status)];

    private static JsonElement Pending(IEnumerable<JsonElement> suggestions, string language, string field = "description") =>
        suggestions.Single(s => s.GetProperty("language").GetString() == language && s.GetProperty("field").GetString() == field);

    private static Dictionary<string, JsonElement> Items(JsonElement job) =>
        job.GetProperty("items").EnumerateArray().ToDictionary(i => $"{i.GetProperty("field").GetString()}/{i.GetProperty("language").GetString()}");

    private async Task<JsonElement> Metadata(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", token)).Body.GetProperty("metadata");

    private async Task<(Guid T, string Admin, Guid Folder, Guid Asset, Guid Job)> Ready(string text = RoadsterText, object? body = null)
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = text });
        using var worker = Worker(); await worker.StartAsync();
        var job = await Run(admin, asset, body ?? new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        return (t, admin, folder, asset, job.GetProperty("id").GetGuid());
    }

    // ---- the glossary guarantee ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_locked_glossary_term_leaves_the_building_as_a_token_and_comes_back_byte_for_byte_in_every_language()
    {
        var (t, admin, folder) = await OpenAiTenant();
        await Term(admin, "Acme Roadster X");
        var asset = await Described(admin, folder, new { en = RoadsterText });
        await DropLeftoverWork();
        using var worker = Worker(); await worker.StartAsync();
        OpenAi.TranslateRequests.Clear();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());

        // what the provider saw: the word is not there, a token is, and the instruction to copy tokens unchanged
        var sent = OpenAi.TranslateRequests.ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, r =>
        {
            Assert.Equal("Meet the ⟦G1⟧. A coastal convertible.", r.Text);
            Assert.DoesNotContain("Roadster", r.Text);
            Assert.Contains("placeholders", r.System);
            Assert.Contains("exactly as written", r.System);
        });
        Assert.Equal(["hi", "ta"], sent.Select(r => r.To).Order());
        Assert.All(sent, r => Assert.Equal("en", r.From));

        // what was stored: the rest translated (shouted, by the fake), the term exactly as written
        var pending = await Suggestions(admin, asset);
        Assert.Equal(2, pending.Count);
        Assert.Equal("hi: MEET THE Acme Roadster X. A COASTAL CONVERTIBLE.", Pending(pending, "hi").GetProperty("value").GetString());
        Assert.Equal("ta: MEET THE Acme Roadster X. A COASTAL CONVERTIBLE.", Pending(pending, "ta").GetProperty("value").GetString());
        Assert.All(pending, s => Assert.Equal(("translation", "description", "en", "gpt-4o-mini", false), (s.GetProperty("kind").GetString(), s.GetProperty("field").GetString(),
            s.GetProperty("sourceLanguage").GetString(), s.GetProperty("model").GetString(), s.GetProperty("sourceStale").GetBoolean())));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM ai_suggestions WHERE kind = 'translation' AND status = 'pending'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM translation_jobs WHERE status = 'succeeded'"));

        // nothing is written into the asset until a person accepts
        Assert.Equal(["en"], (await Metadata(admin, asset)).GetProperty("description").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_mapped_term_gets_the_rendering_of_the_target_language_and_stays_as_written_where_there_is_none()
    {
        var (_, admin, folder) = await OpenAiTenant();
        await Term(admin, "Acme Pulse", "mapped", new Dictionary<string, string> { ["hi"] = "एक्मे पल्स", ["ta"] = "அக்மி பல்ஸ்" });
        var asset = await Described(admin, folder, new { en = "The Acme Pulse is silent." });
        using var worker = Worker(); await worker.StartAsync();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta", "te" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        var pending = await Suggestions(admin, asset);
        Assert.Equal("hi: THE एक्मे पल्स IS SILENT.", Pending(pending, "hi").GetProperty("value").GetString());
        Assert.Equal("ta: THE அக்மி பல்ஸ் IS SILENT.", Pending(pending, "ta").GetProperty("value").GetString());
        Assert.Equal("te: THE Acme Pulse IS SILENT.", Pending(pending, "te").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("mangle")]
    [InlineData("duplicate")]
    [InlineData("invent")]
    [InlineData("translate-the-token")]
    public async Task A_provider_that_drops_changes_repeats_or_invents_a_token_fails_the_item_and_nothing_is_stored(string misbehaviour)
    {
        var (t, admin, folder) = await OpenAiTenant();
        await Term(admin, "Acme Roadster X");
        var asset = await Described(admin, folder, new { en = RoadsterText });
        OpenAi.TranslateWith = (text, _, to) => misbehaviour switch
        {
            "drop" => $"{to}: {text.Replace("⟦G1⟧", "", StringComparison.Ordinal)}",
            "mangle" => $"{to}: {text.Replace("⟦G1⟧", "⟦ G1 ⟧", StringComparison.Ordinal)}",
            "duplicate" => $"{to}: {text} {text}",
            "invent" => $"{to}: {text} ⟦G7⟧",
            _ => $"{to}: {text.Replace("⟦G1⟧", "⟦जी1⟧", StringComparison.Ordinal)}",
        };
        using var worker = Worker(); await worker.StartAsync();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("failed", job.GetProperty("status").GetString());
        var item = Items(job)["description/hi"];
        Assert.Equal("failed", item.GetProperty("status").GetString());
        Assert.Contains("changed a protected term", item.GetProperty("reason").GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_suggestions WHERE kind = 'translation'"));
        Assert.Empty(await Suggestions(admin, asset));
        Assert.Equal(["en"], (await Metadata(admin, asset)).GetProperty("description").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task One_language_failing_leaves_the_others_stored_and_the_job_partial()
    {
        var (_, admin, folder) = await OpenAiTenant();
        await Term(admin, "Acme Roadster X");
        var asset = await Described(admin, folder, new { en = RoadsterText });
        OpenAi.TranslateWith = (text, _, to) => to == "ta" ? text.Replace("⟦G1⟧", "", StringComparison.Ordinal) : $"{to}: {text}";
        using var worker = Worker(); await worker.StartAsync();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("partial", job.GetProperty("status").GetString());
        var items = Items(job);
        Assert.Equal(("ok", "failed"), (items["description/hi"].GetProperty("status").GetString(), items["description/ta"].GetProperty("status").GetString()));
        var pending = await Suggestions(admin, asset);
        Assert.Equal(["hi"], pending.Select(s => s.GetProperty("language").GetString()));
        Assert.Contains("Acme Roadster X", pending[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_glossary_term_is_applied_to_the_tenant_that_owns_it_and_to_no_other()
    {
        var (_, adminA, folderA) = await OpenAiTenant();
        var (tb, adminB, folderB) = await OpenAiTenant();
        await Term(adminA, "Acme Roadster X");
        await Term(adminB, "Acme Roadster X", "mapped", new Dictionary<string, string> { ["hi"] = "एक्मे रोडस्टर" });      // the same text may be a different term in another tenant
        var other = await Described(adminB, folderB, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();
        OpenAi.TranslateRequests.Clear();

        var jobB = await Run(adminB, other, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("succeeded", jobB.GetProperty("status").GetString());
        Assert.Equal("hi: MEET THE एक्मे रोडस्टर. A COASTAL CONVERTIBLE.", Pending(await Suggestions(adminB, other), "hi").GetProperty("value").GetString());

        // tenant A's glossary is invisible to B, and the other way round
        Assert.Equal(1, Count(tb, "SELECT count(*) FROM glossary_terms"));
        Assert.Single((await Call(HttpMethod.Get, "/api/v1/glossary", adminB)).Body.GetProperty("items").EnumerateArray());
        _ = folderA;
    }

    // ---- accepting -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_one_language_merges_it_into_the_field_and_leaves_every_other_language_intact()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText, fr = "Une décapotable côtière." });
        using var worker = Worker(); await worker.StartAsync();
        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        var pending = await Suggestions(admin, asset);
        var hi = Pending(pending, "hi");
        var edits = Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.metadata.updated' AND entity_id = '" + asset + "'");

        var accepted = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi.GetProperty("id").GetGuid()}/accept", admin);
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(1, accepted.Body.GetProperty("changed").GetInt32());

        var description = (await Metadata(admin, asset)).GetProperty("description");
        Assert.Equal(["en", "fr", "hi"], description.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(RoadsterText, description.GetProperty("en").GetString());                                // untouched
        Assert.Equal("Une décapotable côtière.", description.GetProperty("fr").GetString());                  // untouched
        Assert.StartsWith("hi: MEET THE", description.GetProperty("hi").GetString());

        // the other translation still waits; the accepted one is recorded as accepted
        Assert.Equal(["ta"], (await Suggestions(admin, asset)).Select(s => s.GetProperty("language").GetString()));
        Assert.Single(await Suggestions(admin, asset, "accepted"));
        // the same path as a manual edit: audited, and the index follows
        Assert.Equal(edits + 1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.metadata.updated' AND entity_id = '" + asset + "'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.ai.suggestion.decided'"));
        var doc = await IndexedDoc(t, asset, d => d.TryGetProperty("metadata", out var m) && m.TryGetProperty("description", out var de) && de.TryGetProperty("hi", out _));
        Assert.Equal(description.GetProperty("hi").GetString(), doc.GetProperty("metadata").GetProperty("description").GetProperty("hi").GetString());

        // it cannot be accepted twice
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi.GetProperty("id").GetGuid()}/accept", admin)).Status);
    }

    [Fact]
    public async Task Accepting_over_existing_text_is_a_conflict_unless_replace_is_set_and_accept_all_leaves_such_translations_pending()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText, hi = "पुराना पाठ" });
        using var worker = Worker(); await worker.StartAsync();
        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" }, overwrite = true });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        var pending = await Suggestions(admin, asset);
        Assert.Equal(2, pending.Count);
        var hi = Pending(pending, "hi").GetProperty("id").GetGuid();

        var conflict = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi}/accept", admin);
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Contains("already has text in 'hi'", conflict.Body.GetProperty("title").GetString());
        Assert.Equal("पुराना पाठ", (await Metadata(admin, asset)).GetProperty("description").GetProperty("hi").GetString());       // nothing changed
        Assert.Equal(2, (await Suggestions(admin, asset)).Count);

        // accept-all applies what it can: Tamil goes in, Hindi stays pending with a note
        var all = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/accept-all?kind=translation", admin);
        Assert.Equal(HttpStatusCode.OK, all.Status);
        Assert.Equal(1, all.Body.GetProperty("changed").GetInt32());
        Assert.Contains("hi", string.Join(' ', all.Body.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
        Assert.Equal(["hi"], (await Suggestions(admin, asset)).Select(s => s.GetProperty("language").GetString()));

        var replaced = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi}/accept", admin, new { replace = true });
        Assert.Equal(HttpStatusCode.OK, replaced.Status);
        var description = (await Metadata(admin, asset)).GetProperty("description");
        Assert.StartsWith("hi: MEET THE", description.GetProperty("hi").GetString());
        Assert.StartsWith("ta: MEET THE", description.GetProperty("ta").GetString());
        Assert.Equal(RoadsterText, description.GetProperty("en").GetString());
    }

    [Fact]
    public async Task A_reviewer_can_edit_the_translation_while_accepting_it_and_what_they_wrote_is_what_is_stored()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();
        await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        var hi = Pending(await Suggestions(admin, asset), "hi").GetProperty("id").GetGuid();

        var blank = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi}/accept", admin, new { text = "   " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.Status);
        Assert.True(blank.Body.GetProperty("errors").TryGetProperty("text", out _));
        Assert.Single(await Suggestions(admin, asset));                                                                    // still pending

        var accepted = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi}/accept", admin, new { text = "  एक्मे रोडस्टर X से मिलिए।  " });
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal("एक्मे रोडस्टर X से मिलिए।", (await Metadata(admin, asset)).GetProperty("description").GetProperty("hi").GetString());
        var decided = (await Suggestions(admin, asset, "accepted")).Single();
        Assert.Equal("एक्मे रोडस्टर X से मिलिए।", decided.GetProperty("value").GetString());                                // the record shows what was accepted
    }

    [Fact]
    public async Task A_text_longer_than_the_field_allows_is_refused_on_accept_and_nothing_changes()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();
        await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        var hi = Pending(await Suggestions(admin, asset), "hi").GetProperty("id").GetGuid();

        var tooLong = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{hi}/accept", admin, new { text = new string('अ', 5001) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.Status);
        Assert.True(tooLong.Body.GetProperty("errors").TryGetProperty("metadata.description", out _));
        Assert.Equal(["en"], (await Metadata(admin, asset)).GetProperty("description").EnumerateObject().Select(p => p.Name));
        Assert.Single(await Suggestions(admin, asset));
    }

    // ---- requesting ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Languages_that_already_have_text_are_skipped_unless_overwrite_is_asked_for()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText, hi = "मौजूदा पाठ" });
        await DropLeftoverWork();
        using var worker = Worker(); await worker.StartAsync();
        OpenAi.TranslateRequests.Clear();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        var items = Items(job);
        Assert.Equal("skipped", items["description/hi"].GetProperty("status").GetString());
        Assert.Contains("Already has text in 'hi'", items["description/hi"].GetProperty("reason").GetString());
        Assert.Equal("ok", items["description/ta"].GetProperty("status").GetString());
        Assert.Single(OpenAi.TranslateRequests);                                                                              // the skipped one never reached the provider
        Assert.Equal(["ta"], (await Suggestions(admin, asset)).Select(s => s.GetProperty("language").GetString()));
    }

    [Fact]
    public async Task A_field_with_no_text_in_the_named_source_language_is_refused_and_without_a_named_source_the_language_that_has_text_is_used()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { hi = "सिर्फ हिंदी" });
        using var worker = Worker(); await worker.StartAsync();

        // the field has Hindi only: English is requested as the source, which has no text
        var refused = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", admin, new { fields = new[] { "description" }, targetLanguages = new[] { "ta" }, sourceLanguage = "en" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Contains("None of these fields has text", refused.Body.GetProperty("errors").GetProperty("fields")[0].GetString());

        // without a named source the only language with text is used
        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        Assert.Equal("hi", Pending(await Suggestions(admin, asset), "ta").GetProperty("sourceLanguage").GetString());
    }

    [Fact]
    public async Task A_new_request_replaces_a_pending_translation_and_a_rejected_one_never_blocks_a_new_request()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();
        var body = new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } };

        await Run(admin, asset, body);
        var first = Pending(await Suggestions(admin, asset), "hi");
        OpenAi.TranslateWith = (text, _, to) => $"{to}: SECOND {text}";
        await Run(admin, asset, body);
        var second = Pending(await Suggestions(admin, asset), "hi");
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());                                // replaced in place, not added
        Assert.StartsWith("hi: SECOND", second.GetProperty("value").GetString());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM ai_suggestions WHERE kind = 'translation'"));

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{second.GetProperty("id").GetGuid()}/reject", admin)).Status);
        Assert.Empty(await Suggestions(admin, asset));
        OpenAi.TranslateWith = null;
        await Run(admin, asset, body);
        Assert.Single(await Suggestions(admin, asset));
        Assert.Single(await Suggestions(admin, asset, "rejected"));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM ai_suggestions WHERE kind = 'translation'"));
    }

    [Fact]
    public async Task A_translation_says_when_its_source_text_has_changed_since()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();
        await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.False(Pending(await Suggestions(admin, asset), "hi").GetProperty("sourceStale").GetBoolean());

        var edit = await Call(HttpMethod.Patch, $"/api/v1/assets/{asset}", admin, new { metadata = new { description = new { en = "A different description." } } });
        Assert.Equal(HttpStatusCode.OK, edit.Status);
        var stale = Pending(await Suggestions(admin, asset), "hi");
        Assert.True(stale.GetProperty("sourceStale").GetBoolean());
        Assert.Equal("pending", stale.GetProperty("status").GetString());                                                   // still there to accept, with a warning
    }

    [Fact]
    public async Task The_jobs_of_an_asset_are_listed_newest_first_at_most_ten_with_every_outcome()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();
        Guid last = default;
        for (var i = 0; i < 11; i++) last = (await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } })).GetProperty("id").GetGuid();

        var jobs = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/translations", admin)).Body.GetProperty("jobs");
        Assert.Equal(10, jobs.GetArrayLength());
        Assert.Equal(last, jobs[0].GetProperty("id").GetGuid());
        var newest = jobs[0];
        Assert.Equal(("succeeded", false), (newest.GetProperty("status").GetString(), newest.GetProperty("overwrite").GetBoolean()));
        Assert.Equal(["description"], Strings(newest.GetProperty("fields")));
        Assert.Equal(["hi"], Strings(newest.GetProperty("targetLanguages")));
        Assert.NotEqual(JsonValueKind.Null, newest.GetProperty("requestedBy").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, newest.GetProperty("finishedAt").ValueKind);
        Assert.Equal("ok", newest.GetProperty("items")[0].GetProperty("status").GetString());
    }

    // ---- refusals -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Translating_with_ai_off_for_the_tenant_is_a_conflict_with_a_message()
    {
        var (_, admin, folder) = await TenantWithPacks();                       // never enabled
        var asset = await Described(admin, folder, new { en = RoadsterText });
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", admin, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Contains("not enabled", r.Body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Translating_with_a_provider_that_cannot_translate_is_a_clear_conflict()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin, "self-hosted");
        var asset = await Described(admin, folder, new { en = RoadsterText });
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", admin, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        var message = r.Body.GetProperty("title").GetString();
        Assert.Contains("'self-hosted' AI provider cannot translate", message);
        Assert.Contains("OpenAI-compatible", message);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM translation_jobs"));
        var caps = (await Call(HttpMethod.Get, "/api/v1/ai/capabilities", admin)).Body;
        Assert.Equal((false, true), (caps.GetProperty("translate").GetBoolean(), caps.GetProperty("summarise").GetBoolean()));
    }

    [Fact]
    public async Task Only_multilingual_text_fields_of_the_assets_type_can_be_translated()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.Contains("not a multilingual text (ml_text) field", (await Errors(new { fields = new[] { "credit" }, targetLanguages = new[] { "hi" } })).GetProperty("fields")[0].GetString());
        Assert.Contains("is not a field of image assets", (await Errors(new { fields = new[] { "nope" }, targetLanguages = new[] { "hi" } })).GetProperty("fields")[0].GetString());
        Assert.Contains("not a multilingual text", (await Errors(new { fields = new[] { "channels" }, targetLanguages = new[] { "hi" } })).GetProperty("fields")[0].GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM translation_jobs"));
    }

    [Fact]
    public async Task The_limits_on_fields_languages_and_source_length_are_enforced()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.Contains("At most 20", (await Errors(new { fields = Enumerable.Range(0, 21).Select(i => $"f{i}"), targetLanguages = new[] { "hi" } })).GetProperty("fields")[0].GetString());
        Assert.Contains("At most 10", (await Errors(new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "mr", "ta", "te", "bn", "gu", "kn", "ml", "pa", "fr", "de" } })).GetProperty("targetLanguages")[0].GetString());
        Assert.True((await Errors(new { fields = Array.Empty<string>(), targetLanguages = new[] { "hi" } })).TryGetProperty("fields", out _));
        Assert.True((await Errors(new { fields = new[] { "description" }, targetLanguages = new[] { "not a code" } })).TryGetProperty("targetLanguages", out _));
        Assert.True((await Errors(new { fields = new[] { "description" }, targetLanguages = new[] { "hi" }, sourceLanguage = "hi" })).TryGetProperty("targetLanguages", out _));

        // a field that states a generous limit of its own: its text is translated up to 20,000 characters and no more
        var schema = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray()
            .Select(f => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText())!).ToList<object>();
        schema.Add(Field("long_copy", "ml_text", d => d["maxLength"] = 30_000));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = schema })).Status);
        var big = await NewAsset(admin, folder, "image", "Long copy", metadata: new { long_copy = new { en = new string('a', 20_001) } });
        Assert.Contains("longer than 20,000 characters", (await Call(HttpMethod.Post, $"/api/v1/assets/{big}/translate", admin, new { fields = new[] { "long_copy" }, targetLanguages = new[] { "hi" } }))
            .Body.GetProperty("errors").GetProperty("fields")[0].GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM translation_jobs"));
    }

    [Fact]
    public async Task A_language_the_tenant_does_not_use_is_refused()
    {
        var (t, admin, folder) = await OpenAiTenant();
        Sql($"UPDATE tenants SET settings_json = '{{\"languages\":[\"en\",\"hi\"]}}'::jsonb WHERE id = '{t}'");
        var asset = await Described(admin, folder, new { en = RoadsterText });
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", admin, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Contains("'ta' is not enabled for this tenant", r.Body.GetProperty("errors").GetProperty("targetLanguages")[0].GetString());
    }

    [Fact]
    public async Task Only_someone_who_may_edit_the_asset_can_ask_for_a_translation_and_a_stranger_does_not_learn_it_exists()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        var body = new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", As(t, "Approver"), body)).Status);      // may read the draft, may not edit it
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", As(t, "Editor"), body)).Status);

        var viewer = As(t, "Viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", viewer, body)).Status);            // a draft the viewer cannot read
        var other = await NewTenantAsync(); var outsider = Admin(other);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/translate", outsider, body)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/translations", outsider)).Status);
        Assert.Equal(0, Count(other, "SELECT count(*) FROM translation_jobs"));
    }

    // ---- provider trouble ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_refuses_the_text_or_cuts_the_answer_off_fails_the_item_with_the_reason_and_stores_nothing()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        using var worker = Worker(); await worker.StartAsync();

        OpenAi.TranslateRefused = true;
        var refused = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("failed", refused.GetProperty("status").GetString());
        Assert.Contains("refused", Items(refused)["description/hi"].GetProperty("reason").GetString());

        OpenAi.TranslateRefused = false;
        OpenAi.TranslateCutOff = true;
        var cut = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("failed", cut.GetProperty("status").GetString());
        Assert.Contains("cut off", Items(cut)["description/hi"].GetProperty("reason").GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_suggestions"));
    }

    [Fact]
    public async Task A_translation_longer_than_the_field_allows_fails_its_item()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var schema = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray()
            .Select(f => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText())!).ToList<object>();
        schema.Add(Field("short_copy", "ml_text", d => d["maxLength"] = 30));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = schema })).Status);
        var asset = await NewAsset(admin, folder, "image", "Short copy", metadata: new { short_copy = new { en = "Twenty five characters ok" } });
        OpenAi.TranslateWith = (text, _, _) => text + text;
        using var worker = Worker(); await worker.StartAsync();

        var job = await Run(admin, asset, new { fields = new[] { "short_copy" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("more than the field allows (30)", Items(job)["short_copy/hi"].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_provider_outage_retries_then_fails_the_job_and_tells_the_requester_while_a_later_request_works()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        OpenAi.Down = true;
        using var worker = Worker(); await worker.StartAsync();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("Gave up after", job.GetProperty("error").GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_suggestions"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.translation.ready'"));

        OpenAi.Down = false;
        var again = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        Assert.Equal("succeeded", again.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Switching_ai_off_before_the_worker_gets_to_the_job_fails_it_without_calling_the_provider()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText });
        await DropLeftoverWork();
        var job = await Translate(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });      // no worker yet: the request waits
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = false })).Status);
        using var worker = Worker(); await worker.StartAsync();
        OpenAi.TranslateRequests.Clear();

        var done = await WaitForJob(admin, asset, job.GetProperty("id").GetGuid());
        Assert.Equal("failed", done.GetProperty("status").GetString());
        Assert.Contains("not enabled", done.GetProperty("error").GetString());
        Assert.Empty(OpenAi.TranslateRequests);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_suggestions"));
    }

    // ---- the review queue ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_review_queue_lists_the_asset_for_people_who_may_edit_it_and_not_for_people_who_may_not()
    {
        var (t, admin, folder) = await OpenAiTenant();             // the folder gives its assets the region "north"
        var role = await Call(HttpMethod.Post, "/api/v1/roles", admin, new
        {
            name = "RegionEditor", permissions = new[] { "assets.read", "assets.read.unpublished", "assets.update" }, attributeRestricted = true, requiredAttributes = new[] { "region" },
        });
        Assert.Equal(HttpStatusCode.Created, role.Status);
        var north = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["RegionEditor"], attributes: new() { ["region"] = ["north"] });
        var south = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["RegionEditor"], attributes: new() { ["region"] = ["south"] });
        var viewer = As(t, "Viewer");
        var asset = await Described(admin, folder, new { en = RoadsterText }, "North brochure");
        using var worker = Worker(); await worker.StartAsync();
        var me = (await Call(HttpMethod.Get, "/api/v1/me", admin)).Body.GetProperty("userId").GetGuid();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());

        async Task<JsonElement> Queue(string token) => (await Call(HttpMethod.Get, "/api/v1/ai/reviews?kind=translation", token)).Body;
        async Task<int> Badge(string token) => (await Call(HttpMethod.Get, "/api/v1/ai/reviews/summary", token)).Body.GetProperty("translation").GetInt32();

        var mine = await Queue(admin);
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal((asset, "North brochure", "image", 2), (item.GetProperty("assetId").GetGuid(), item.GetProperty("title").GetString(), item.GetProperty("assetType").GetString(), item.GetProperty("pending").GetInt32()));
        Assert.Equal(["hi", "ta"], Strings(item.GetProperty("languages")));
        Assert.Equal(["description"], Strings(item.GetProperty("fields")));
        Assert.Equal(me, item.GetProperty("requestedBy").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("oldestAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("nextCursor").ValueKind);
        Assert.Equal(1, await Badge(admin));

        Assert.Single((await Queue(north)).GetProperty("items").EnumerateArray());                                              // the same region: may edit it
        Assert.Equal(1, await Badge(north));
        Assert.Empty((await Queue(south)).GetProperty("items").EnumerateArray());                                               // another region: ABAC hides it
        Assert.Equal(0, await Badge(south));
        Assert.Empty((await Queue(viewer)).GetProperty("items").EnumerateArray());                                              // may read at most: not an editor
        Assert.Equal(0, await Badge(viewer));
        Assert.Single((await Queue(As(t, "Editor"))).GetProperty("items").EnumerateArray());                                    // an Editor can edit every asset: sees it
        Assert.Empty((await Queue(As(t, "Approver"))).GetProperty("items").EnumerateArray());                                   // an Approver can read drafts but not edit them

        // tenants never see each other's queue
        var other = await NewTenantAsync();
        Assert.Empty((await Queue(Admin(other))).GetProperty("items").EnumerateArray());

        // deciding all of them empties the queue
        foreach (var s in await Suggestions(admin, asset))
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{s.GetProperty("id").GetGuid()}/reject", admin)).Status);
        Assert.Empty((await Queue(admin)).GetProperty("items").EnumerateArray());
        Assert.Equal(0, await Badge(admin));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, "/api/v1/ai/reviews?kind=tag", admin)).Status);
    }

    [Fact]
    public async Task The_review_queue_is_oldest_first_and_pages_with_a_cursor()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var assets = new List<Guid>();
        for (var i = 0; i < 3; i++) assets.Add(await Described(admin, folder, new { en = RoadsterText }, $"Brochure {i}"));
        using var worker = Worker(); await worker.StartAsync();
        foreach (var a in assets) await Run(admin, a, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });

        var page1 = (await Call(HttpMethod.Get, "/api/v1/ai/reviews?limit=2", admin)).Body;
        Assert.Equal(assets.Take(2), page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetId").GetGuid()));
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var page2 = (await Call(HttpMethod.Get, $"/api/v1/ai/reviews?limit=2&cursor={Uri.EscapeDataString(cursor!)}", admin)).Body;
        Assert.Equal([assets[2]], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
    }

    // ---- notification ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_person_who_asked_is_told_when_the_translations_are_ready_and_nobody_else_is()
    {
        var (t, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText }, "Roadster hero");
        var otherAdmin = Admin(t);
        await Call(HttpMethod.Get, "/api/v1/me", otherAdmin);          // provisioned: a person who could have been told
        using var notify = NotifyHosts.Worker(Pg, _mq, configure: svc => svc.AddSingleton<IEmailSender>(new FakeEmailSender()));
        await notify.StartAsync();
        using var worker = Worker(); await worker.StartAsync();

        var job = await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi", "ta" } });
        Assert.Equal("succeeded", job.GetProperty("status").GetString());

        var mine = await NotifyHosts.Eventually(async () => (await Call(HttpMethod.Get, "/api/v1/notifications", admin)).Body.GetProperty("items").EnumerateArray().ToList(), n => n.Count >= 1);
        var note = Assert.Single(mine);
        Assert.Equal(("asset.translation.ready", "translation"), (note.GetProperty("type").GetString(), note.GetProperty("category").GetString()));
        Assert.Contains("Translations ready to review", note.GetProperty("title").GetString());
        Assert.Contains("2 translations", note.GetProperty("body").GetString());
        Assert.Equal($"/library/assets/{asset}", note.GetProperty("linkUrl").GetString());
        await Task.Delay(1000);
        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/notifications", otherAdmin)).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/notifications", admin)).Body.GetProperty("items").GetArrayLength());           // once, not per consumer retry
    }

    [Fact]
    public async Task A_failed_job_is_reported_to_the_requester_as_a_failure()
    {
        var (_, admin, folder) = await OpenAiTenant();
        var asset = await Described(admin, folder, new { en = RoadsterText }, "Roadster hero");
        OpenAi.TranslateRefused = true;
        using var notify = NotifyHosts.Worker(Pg, _mq, configure: svc => svc.AddSingleton<IEmailSender>(new FakeEmailSender()));
        await notify.StartAsync();
        using var worker = Worker(); await worker.StartAsync();

        await Run(admin, asset, new { fields = new[] { "description" }, targetLanguages = new[] { "hi" } });
        var mine = await NotifyHosts.Eventually(async () => (await Call(HttpMethod.Get, "/api/v1/notifications", admin)).Body.GetProperty("items").EnumerateArray().ToList(), n => n.Count >= 1);
        Assert.StartsWith("Translation failed", Assert.Single(mine).GetProperty("title").GetString());
    }

    // ---- audit ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_translation_request_is_audited_and_the_ready_event_carries_counts_but_never_text()
    {
        var (t, admin, folder, asset, job) = await Ready();
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.translation.requested' AND entity_id = '" + asset + "'"));
        var payload = JsonDocument.Parse(SingleString(t, "SELECT payload::text FROM outbox WHERE event_type = 'asset.translation.ready'")).RootElement;
        Assert.Equal(job, payload.GetProperty("JobId").GetGuid());
        Assert.Equal((2, 0, 0, "succeeded"), (payload.GetProperty("Translated").GetInt32(), payload.GetProperty("Skipped").GetInt32(), payload.GetProperty("Failed").GetInt32(), payload.GetProperty("Status").GetString()));
        Assert.DoesNotContain("COASTAL", payload.GetRawText());
        Assert.DoesNotContain("coastal", payload.GetRawText());
        _ = (admin, folder);
    }

    private string SingleString(Guid tenant, string sql)
    {
        using var c = Pg.RawAppConnection(tenant);
        using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return (string)cmd.ExecuteScalar()!;
    }

    // ---- the glossary API -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_glossary_is_managed_by_admins_read_by_anyone_who_reads_assets_and_every_change_is_audited()
    {
        var t = await NewTenantAsync(); var admin = Admin(t); var editor = As(t, "Editor"); var viewer = As(t, "Viewer");

        var created = await Call(HttpMethod.Post, "/api/v1/glossary", admin, new { term = " Acme Roadster X ", mode = "locked", note = "Model name" });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Body.GetProperty("id").GetGuid();
        Assert.Equal(("Acme Roadster X", "locked", "Model name"), (created.Body.GetProperty("term").GetString(), created.Body.GetProperty("mode").GetString(), created.Body.GetProperty("note").GetString()));
        Assert.Empty(created.Body.GetProperty("translations").EnumerateObject());
        var pulse = await Call(HttpMethod.Post, "/api/v1/glossary", admin, new { term = "Acme Pulse", mode = "mapped", translations = new { HI = " एक्मे पल्स " } });
        Assert.Equal("एक्मे पल्स", pulse.Body.GetProperty("translations").GetProperty("hi").GetString());

        // anyone who can read assets can read the glossary; only an admin can change it
        Assert.Equal(2, (await Call(HttpMethod.Get, "/api/v1/glossary", viewer)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/glossary", editor, new { term = "x", mode = "locked" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"/api/v1/glossary/{id}", editor, new { term = "x", mode = "locked" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/glossary/{id}", editor)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/v1/glossary", "not-a-token")).Status);
        Assert.Contains("glossary.manage", (await Call(HttpMethod.Get, "/api/v1/permissions", admin)).Body.EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain("glossary.manage", (await Call(HttpMethod.Get, "/api/v1/me", editor)).Body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        var updated = await Call(HttpMethod.Put, $"/api/v1/glossary/{id}", admin, new { term = "Acme Roadster X2", mode = "mapped", translations = new { ta = "அக்மி" } });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal(("Acme Roadster X2", "mapped"), (updated.Body.GetProperty("term").GetString(), updated.Body.GetProperty("mode").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/glossary/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/glossary/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/glossary/{id}", admin, new { term = "y", mode = "locked" })).Status);

        Assert.Equal((1, 1, 1), (Count(t, "SELECT count(*) FROM audit_log WHERE action = 'glossary.term.updated'"), Count(t, "SELECT count(*) FROM audit_log WHERE action = 'glossary.term.deleted'"),
            Count(t, "SELECT count(*) FROM audit_log WHERE action = 'glossary.term.created' AND entity_id = '" + id + "'")));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'glossary.term.created'"));
    }

    [Fact]
    public async Task Glossary_terms_are_validated_and_unique_within_a_tenant_but_not_across_tenants()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var adminA = Admin(a); var adminB = Admin(b);
        async Task<JsonElement> Errors(object body, string? token = null)
        {
            var r = await Call(HttpMethod.Post, "/api/v1/glossary", token ?? adminA, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Errors(new { term = "", mode = "locked" })).TryGetProperty("term", out _));
        Assert.True((await Errors(new { term = "Acme", mode = "frozen" })).TryGetProperty("mode", out _));
        Assert.True((await Errors(new { term = "Acme", mode = "mapped" })).TryGetProperty("translations", out _));
        Assert.True((await Errors(new { term = "Acme", mode = "mapped", translations = new { xx_yy = "a" } })).TryGetProperty("translations", out _));
        Assert.True((await Errors(new { term = "Acme ⟦G1⟧", mode = "locked" })).TryGetProperty("term", out _));
        Assert.True((await Errors(new { term = new string('a', 201), mode = "locked" })).TryGetProperty("term", out _));

        await Term(adminA, "Acme Roadster X");
        Assert.Contains("already in the glossary", (await Errors(new { term = "Acme Roadster X", mode = "locked" })).GetProperty("term")[0].GetString());
        await Term(adminA, "acme roadster x");                                                                           // matching is case-sensitive, so this is another term
        await Term(adminB, "Acme Roadster X");                                                                           // and another tenant has its own
        var lower = (await Call(HttpMethod.Get, "/api/v1/glossary?q=ROADSTER", adminA)).Body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("term").GetString() == "acme roadster x").GetProperty("id").GetGuid();
        var rename = await Call(HttpMethod.Put, $"/api/v1/glossary/{lower}", adminA, new { term = "Acme Roadster X", mode = "locked" });       // would collide with the first term
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rename.Status);
        Assert.True(rename.Body.GetProperty("errors").TryGetProperty("term", out _));
        var id = lower;

        Assert.Equal(2, Count(a, "SELECT count(*) FROM glossary_terms"));
        Assert.Equal(1, Count(b, "SELECT count(*) FROM glossary_terms"));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/glossary/{id}", adminB)).Status);                // another tenant's term is not even visible
        Assert.Equal(2, Count(a, "SELECT count(*) FROM glossary_terms"));
    }

    [Fact]
    public async Task The_glossary_is_alphabetical_filterable_and_paged_by_term_and_holds_at_most_five_thousand_terms()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        foreach (var term in new[] { "Delta Pro", "alpha one", "Charlie", "Acme Roadster X", "Bravo" }) await Term(admin, term);

        // the order is the database's own (by term); what matters is that it is stable and that pages follow on from each other
        var all = (await Call(HttpMethod.Get, "/api/v1/glossary", admin)).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("term").GetString()!).ToList();
        Assert.Equal(["Acme Roadster X", "Bravo", "Charlie", "Delta Pro", "alpha one"], all.Order(StringComparer.Ordinal));
        var page1 = (await Call(HttpMethod.Get, "/api/v1/glossary?limit=2", admin)).Body;
        Assert.Equal(all.Take(2), page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("term").GetString()!));
        var page2 = (await Call(HttpMethod.Get, $"/api/v1/glossary?limit=2&cursor={Uri.EscapeDataString(page1.GetProperty("nextCursor").GetString()!)}", admin)).Body;
        Assert.Equal(all.Skip(2).Take(2), page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("term").GetString()!));
        var page3 = (await Call(HttpMethod.Get, $"/api/v1/glossary?limit=2&cursor={Uri.EscapeDataString(page2.GetProperty("nextCursor").GetString()!)}", admin)).Body;
        Assert.Equal(all.Skip(4), page3.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("term").GetString()!));
        Assert.Equal(JsonValueKind.Null, page3.GetProperty("nextCursor").ValueKind);
        Assert.Equal(["Acme Roadster X"], (await Call(HttpMethod.Get, "/api/v1/glossary?q=roadster", admin)).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("term").GetString()!));

        Sql($"INSERT INTO glossary_terms (id, tenant_id, term, mode, translations, note, created_at, updated_at) SELECT gen_random_uuid(), '{t}', 'Bulk ' || n, 'locked', '{{}}'::jsonb, '', now(), now() FROM generate_series(1, 4995) n");
        Assert.Equal(5000, Count(t, "SELECT count(*) FROM glossary_terms"));
        var over = await Call(HttpMethod.Post, "/api/v1/glossary", admin, new { term = "One too many", mode = "locked" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.Status);
        Assert.Contains("at most 5,000", over.Body.GetProperty("errors").GetProperty("term")[0].GetString());
    }

    // ---- summaries -------------------------------------------------------------------------------------------------------------------------------------

    private const string OcrPage = "The Acme showroom opens at nine. Zephyrine is the secret word. We close at six.";

    [Fact]
    public async Task A_three_page_pdf_gets_a_summary_when_it_is_ingested_that_is_stored_shown_and_searchable_and_a_two_page_pdf_does_not()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);                                            // self-hosted: the summary is extractive and made in the worker
        Sidecar.OcrText = OcrPage;
        using var worker = Worker(); await worker.StartAsync();

        var three = await IngestedAsset(admin, folder, TestImages.Pdf("Guide " + NextVariant(), 3), "document", "guide.pdf", "application/pdf", "Showroom guide");
        var ai = await WaitForAi(admin, three, "ready", "failed");
        Assert.Equal("ready", ai.GetProperty("status").GetString());
        var step = ai.GetProperty("run").GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "summary");
        Assert.Equal(("ok", "extractive-v1"), (step.GetProperty("status").GetString(), step.GetProperty("model").GetString()));
        var summary = ai.GetProperty("summary");
        Assert.Equal(("extractive-v1", "en"), (summary.GetProperty("model").GetString(), summary.GetProperty("language").GetString()));
        Assert.Contains("Zephyrine is the secret word", summary.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("createdAt").ValueKind);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM asset_extracts WHERE summary IS NOT NULL AND summary_language = 'en'"));

        // indexed, and found by it
        var doc = await IndexedDoc(t, three, d => d.TryGetProperty("summary_text", out _));
        Assert.Contains("Zephyrine", doc.GetProperty("summary_text").GetString());
        await RefreshAsync(t);
        var found = (await Call(HttpMethod.Post, "/api/v1/search", admin, new { query = "zephyrine" })).Body;
        Assert.Equal(three, found.GetProperty("hits")[0].GetProperty("id").GetGuid());
        Assert.Contains("summary", Strings(found.GetProperty("hits")[0].GetProperty("matchedBy")));

        // two pages: no summary, and the record says why
        var two = await IngestedAsset(admin, folder, TestImages.Pdf("Leaflet " + NextVariant(), 2), "document", "leaflet.pdf", "application/pdf", "Showroom leaflet");
        var aiTwo = await WaitForAi(admin, two, "ready", "failed");
        Assert.Equal(JsonValueKind.Null, aiTwo.GetProperty("summary").ValueKind);
        var skipped = aiTwo.GetProperty("run").GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "summary");
        Assert.Equal("skipped", skipped.GetProperty("status").GetString());
        Assert.Contains("more than 2 pages", skipped.GetProperty("detail").GetString());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM asset_extracts WHERE summary IS NOT NULL"));
        Assert.DoesNotContain("summary_text", (await IndexedDoc(t, two, d => d.TryGetProperty("ocr_text", out _))).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task With_the_openai_compatible_provider_the_model_writes_the_summary_and_a_word_only_in_the_summary_finds_the_document()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin, "openai-compatible");
        OpenAi.OcrText = OcrPage;
        using var worker = Worker(); await worker.StartAsync();

        var asset = await IngestedAsset(admin, folder, TestImages.Pdf("Guide " + NextVariant(), 3), "document", "guide.pdf", "application/pdf", "Showroom guide");
        var ai = await WaitForAi(admin, asset, "ready", "failed");
        Assert.Equal("gpt-4o-mini", ai.GetProperty("summary").GetProperty("model").GetString());
        Assert.EndsWith("summarymarker", ai.GetProperty("summary").GetProperty("text").GetString());
        Assert.Equal(1, OpenAi.Summaries);

        await IndexedDoc(t, asset, d => d.TryGetProperty("summary_text", out _));
        await RefreshAsync(t);
        var found = (await Call(HttpMethod.Post, "/api/v1/search", admin, new { query = "summarymarker" })).Body;
        Assert.Equal(asset, found.GetProperty("hits")[0].GetProperty("id").GetGuid());
        Assert.Equal(["summary"], Strings(found.GetProperty("hits")[0].GetProperty("matchedBy")));        // the words are in the summary alone, not in the text read from the pages
    }

    [Fact]
    public async Task Summaries_switched_off_in_the_settings_are_not_made()
    {
        var (_, admin, folder) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, features = new { tags = true, ocr = true, transcribe = true, embeddings = true, summaries = false } })).Status);
        Sidecar.OcrText = OcrPage;
        using var worker = Worker(); await worker.StartAsync();
        var asset = await IngestedAsset(admin, folder, TestImages.Pdf("Guide " + NextVariant(), 3), "document", "guide.pdf", "application/pdf", "Showroom guide");
        var ai = await WaitForAi(admin, asset, "ready", "failed");
        Assert.Equal(JsonValueKind.Null, ai.GetProperty("summary").ValueKind);
        var step = ai.GetProperty("run").GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "summary");
        Assert.Equal("skipped", step.GetProperty("status").GetString());
        Assert.Contains("Switched off", step.GetProperty("detail").GetString());

        var manual = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/summarise", admin);
        Assert.Equal(HttpStatusCode.Conflict, manual.Status);
        Assert.Contains("switched off", manual.Body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_summary_can_be_asked_for_by_hand_for_any_asset_that_has_extracted_text_and_the_refusals_say_why()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var noAi = await NewAsset(admin, folder, "document", "Never analysed");
        var refusedOff = await Call(HttpMethod.Post, $"/api/v1/assets/{noAi}/ai/summarise", admin);
        Assert.Equal(HttpStatusCode.Conflict, refusedOff.Status);                                      // no file yet is the first thing it finds
        Assert.Contains("no file", refusedOff.Body.GetProperty("title").GetString());

        await EnableAi(admin);
        Sidecar.OcrText = OcrPage;
        using var worker = Worker(); await worker.StartAsync();
        var two = await IngestedAsset(admin, folder, TestImages.Pdf("Leaflet " + NextVariant(), 2), "document", "leaflet.pdf", "application/pdf", "Showroom leaflet");
        var before = await WaitForAi(admin, two, "ready", "failed");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("summary").ValueKind);

        var asked = await Call(HttpMethod.Post, $"/api/v1/assets/{two}/ai/summarise", admin);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        JsonElement after = default;
        for (var i = 0; i < 100 && after.ValueKind != JsonValueKind.Object; i++)
        {
            var ai = (await Call(HttpMethod.Get, $"/api/v1/assets/{two}/ai", admin)).Body;
            // the answer is read in several steps, so wait until the step that records the summary shows too
            if (ai.GetProperty("summary").ValueKind == JsonValueKind.Object
                && ai.GetProperty("run").GetProperty("steps").EnumerateArray().Any(s => s.GetProperty("name").GetString() == "summary" && s.GetProperty("status").GetString() == "ok")) after = ai;
            else await Task.Delay(100);
        }
        Assert.Equal(JsonValueKind.Object, after.ValueKind);
        Assert.Contains("Zephyrine", after.GetProperty("summary").GetProperty("text").GetString());
        var step = after.GetProperty("run").GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "summary");
        Assert.Equal("ok", step.GetProperty("status").GetString());
        Assert.Contains("on request", step.GetProperty("detail").GetString());
        Assert.Equal("ready", after.GetProperty("status").GetString());                                // the run itself is untouched
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.ai.summarise.requested'"));
        await IndexedDoc(t, two, d => d.TryGetProperty("summary_text", out _));

        // a picture with no text in it has nothing to summarise
        Sidecar.OcrText = "";
        var picture = await IngestedAsset(admin, folder, Picture(), "image", "photo.jpg", "image/jpeg");
        await WaitForAi(admin, picture, "ready", "failed");
        var none = await Call(HttpMethod.Post, $"/api/v1/assets/{picture}/ai/summarise", admin);
        Assert.Equal(HttpStatusCode.Conflict, none.Status);
        Assert.Contains("no extracted text", none.Body.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{two}/ai/summarise", As(t, "Approver"))).Status);        // may read, may not edit
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{two}/ai/summarise", As(t, "Viewer"))).Status);          // cannot even see the draft
    }

    [Fact]
    public async Task The_summary_is_a_setting_that_older_stored_settings_read_as_on()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var s = (await Call(HttpMethod.Get, "/api/v1/ai/settings", admin)).Body;
        Assert.True(s.GetProperty("features").GetProperty("summaries").GetBoolean());

        // a row written before the setting existed has no such key
        Sql($"INSERT INTO ai_settings (tenant_id, enabled, provider, features, ocr_language, updated_at) VALUES ('{t}', true, NULL, '{{\"tags\":true,\"ocr\":false,\"transcribe\":true,\"embeddings\":true}}'::jsonb, NULL, now())");
        var legacy = (await Call(HttpMethod.Get, "/api/v1/ai/settings", admin)).Body.GetProperty("features");
        Assert.Equal((true, false), (legacy.GetProperty("summaries").GetBoolean(), legacy.GetProperty("ocr").GetBoolean()));

        var saved = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, features = new { tags = true, ocr = true, transcribe = true, embeddings = true, summaries = false } });
        Assert.False(saved.Body.GetProperty("features").GetProperty("summaries").GetBoolean());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'ai.settings.changed' AND after ->> 'Summaries' = 'false'"));
        // saving without naming it leaves it on, as for the other switches
        var again = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, features = new { tags = true, ocr = true, transcribe = true, embeddings = true } });
        Assert.True(again.Body.GetProperty("features").GetProperty("summaries").GetBoolean());
    }
}
