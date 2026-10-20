using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Ai;
using Dam.Application.Notifications;
using Dam.Domain.Ai;
using Dam.Domain.Authorization;
using Dam.Domain.Notifications;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Ai;
using Dam.Shared.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dam.UnitTests;

/// <summary>Translation requests, job outcomes, source selection, the glossary rules and the entities behind them (ADR-0036).</summary>
public sealed class TranslationTests
{
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid Asset = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Field(params (string Lang, string Text)[] values) =>
        JsonSerializer.SerializeToElement(values.ToDictionary(v => v.Lang, v => v.Text));

    private static TranslationItem Item(string status, string field = "description", string language = "hi", string? reason = null) =>
        new() { Field = field, Language = language, Status = status, Reason = reason };

    // ---- job outcome ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ok,ok,ok", "succeeded")]
    [InlineData("ok,skipped", "succeeded")]
    [InlineData("skipped,skipped", "succeeded")]          // nothing needed doing: that is not a failure
    [InlineData("ok,failed", "partial")]
    [InlineData("ok,skipped,failed", "partial")]
    [InlineData("failed,failed", "failed")]
    [InlineData("skipped,failed", "failed")]
    [InlineData("failed", "failed")]
    [InlineData("", "failed")]                            // nothing to attempt at all
    public void A_job_is_succeeded_partial_or_failed_by_what_happened_to_its_items(string items, string expected)
    {
        var list = items.Length == 0 ? [] : items.Split(',').Select(s => Item(s)).ToList();
        Assert.Equal(expected, TranslationRules.StatusFor(list));
    }

    [Fact]
    public void A_job_records_its_items_status_and_when_it_finished()
    {
        var job = TranslationJob.Create(Tenant, Asset, ["description", "title_ml"], ["hi", "ta"], null, false, Guid.NewGuid(), Now);
        Assert.Equal(TranslationJobStatuses.Pending, job.Status);
        Assert.False(job.IsFinished);

        job.Start(Now.AddSeconds(1));
        Assert.Equal(TranslationJobStatuses.Running, job.Status);
        job.Start(Now.AddSeconds(5));
        Assert.Equal(Now.AddSeconds(1), job.StartedAt);       // starting again (a retry) keeps the first start

        job.Finish([Item("ok"), Item("failed", language: "ta", reason: "x"), Item("skipped", "title_ml")], Now.AddSeconds(9));
        Assert.Equal((TranslationJobStatuses.Partial, true, Now.AddSeconds(9)), (job.Status, job.IsFinished, job.FinishedAt));
        Assert.Equal((1, 1, 1), (job.Count("ok"), job.Count("skipped"), job.Count("failed")));
    }

    [Fact]
    public void A_job_that_cannot_run_fails_with_a_reason_and_the_reason_is_clipped()
    {
        var job = TranslationJob.Create(Tenant, Asset, ["description"], ["hi"], "en", true, null, Now);
        job.Fail(new string('x', 5000), Now);
        Assert.Equal(TranslationJobStatuses.Failed, job.Status);
        Assert.Equal(1000, job.Error.Length);
        Assert.True(job.IsFinished);
        Assert.Equal(("en", true, null), (job.SourceLanguage, job.Overwrite, job.RequestedBy));
    }

    // ---- languages and hashes ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("hi", "hi")]
    [InlineData(" HI ", "hi")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("zh-Hans-CN", "zh-hans-cn")]
    [InlineData("h", null)]
    [InlineData("1a", null)]
    [InlineData("hi hi", null)]
    [InlineData("hi_in", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("averyveryverylongcode", null)]
    public void Language_codes_are_normalised_like_multilingual_metadata_keys(string? input, string? expected) =>
        Assert.Equal(expected, TranslationRules.NormalizeLanguage(input));

    [Fact]
    public void The_source_hash_is_stable_and_changes_with_the_text()
    {
        Assert.Equal(TranslationRules.Hash("Acme roadster"), TranslationRules.Hash("Acme roadster"));
        Assert.NotEqual(TranslationRules.Hash("Acme roadster"), TranslationRules.Hash("Acme roadster "));
        Assert.Equal(64, TranslationRules.Hash("x").Length);
    }

    [Fact]
    public void The_source_limit_is_the_fields_own_limit_but_never_more_than_twenty_thousand()
    {
        Assert.Equal(20_000, TranslationRules.SourceLimit(null));
        Assert.Equal(500, TranslationRules.SourceLimit(500));
        Assert.Equal(20_000, TranslationRules.SourceLimit(100_000));
    }

    // ---- choosing the source ---------------------------------------------------------------------------------------

    [Fact]
    public void A_requested_source_is_used_or_there_is_none()
    {
        var value = Field(("en", "Hello"), ("hi", "नमस्ते"));
        Assert.Equal(("hi", "नमस्ते"), TranslationRules.ChooseSource(value, "hi", null, "ta"));
        Assert.Null(TranslationRules.ChooseSource(value, "fr", null, "ta"));          // asked for French, which has no text
        Assert.Null(TranslationRules.ChooseSource(value, "hi", null, "hi"));          // the source cannot be the target
    }

    [Fact]
    public void Without_a_request_the_tenant_default_comes_first_then_english_then_any_language_with_text()
    {
        var all = Field(("hi", "नमस्ते"), ("en", "Hello"), ("ta", "வணக்கம்"));
        Assert.Equal("ta", TranslationRules.ChooseSource(all, null, "ta", "te")!.Value.Language);
        Assert.Equal("en", TranslationRules.ChooseSource(all, null, null, "te")!.Value.Language);
        Assert.Equal("en", TranslationRules.ChooseSource(all, null, "fr", "te")!.Value.Language);          // the default has no text here
        var noEnglish = Field(("hi", "नमस्ते"), ("ta", "வணக்கம்"));
        Assert.Equal("hi", TranslationRules.ChooseSource(noEnglish, null, null, "te")!.Value.Language);     // the first one the field has
        Assert.Equal("ta", TranslationRules.ChooseSource(noEnglish, null, null, "hi")!.Value.Language);     // never the target itself
        Assert.Null(TranslationRules.ChooseSource(Field(("hi", "नमस्ते")), null, null, "hi"));
    }

    [Fact]
    public void Blank_or_missing_text_is_not_a_source()
    {
        Assert.Null(TranslationRules.ChooseSource(Field(("en", "   ")), null, null, "hi"));
        Assert.Null(TranslationRules.ChooseSource(null, null, null, "hi"));
        Assert.Null(TranslationRules.ChooseSource(JsonSerializer.SerializeToElement("plain"), null, null, "hi"));
        Assert.Null(TranslationRules.TextOf(Field(("en", "x")), "hi"));
        Assert.Equal("x", TranslationRules.TextOf(Field(("EN", "x")), "en"));        // keys compare ignoring case
    }

    // ---- planning ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_plan_has_one_entry_per_field_and_language_with_a_reason_for_every_skip()
    {
        var fields = new Dictionary<string, JsonElement>
        {
            ["description"] = Field(("en", "Hello"), ("hi", "नमस्ते")),
            ["caption"] = Field(("hi", "नमस्ते")),
            ["empty"] = Field(("en", " ")),
        };
        var plan = TranslationPlanner.Plan(["description", "caption", "empty"], ["hi", "ta"], null, null, overwrite: false, n => fields.TryGetValue(n, out var v) ? v : null);

        Assert.Equal(6, plan.Count);
        var byKey = plan.ToDictionary(w => (w.Field, w.Language));
        Assert.Contains("Already has text in 'hi'", byKey[("description", "hi")].SkipReason);          // overwrite is false
        Assert.False(byKey[("description", "hi")].NoSource);
        Assert.True(byKey[("description", "ta")].ShouldTranslate);
        Assert.Equal(("en", "Hello"), (byKey[("description", "ta")].SourceLanguage, byKey[("description", "ta")].SourceText));
        Assert.Contains("Already has text", byKey[("caption", "hi")].SkipReason);
        Assert.True(byKey[("caption", "ta")].ShouldTranslate);
        Assert.Equal("hi", byKey[("caption", "ta")].SourceLanguage);                                     // the only language it has
        Assert.True(byKey[("empty", "hi")].NoSource);
        Assert.Contains("no text", byKey[("empty", "ta")].SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Overwrite_translates_languages_that_already_have_text_but_never_from_themselves()
    {
        var value = Field(("en", "Hello"), ("hi", "नमस्ते"));
        var plan = TranslationPlanner.Plan(["description"], ["hi"], null, null, overwrite: true, _ => value);
        var work = Assert.Single(plan);
        Assert.True(work.ShouldTranslate);
        Assert.Equal("en", work.SourceLanguage);
    }

    [Fact]
    public void A_named_source_language_is_respected_and_cannot_also_be_a_target()
    {
        var value = Field(("en", "Hello"), ("hi", "नमस्ते"));
        var plan = TranslationPlanner.Plan(["description"], ["ta", "en"], "hi", null, overwrite: true, _ => value);
        Assert.Equal("hi", plan.Single(w => w.Language == "ta").SourceLanguage);
        Assert.True(plan.Single(w => w.Language == "en").ShouldTranslate);                                // en is a target and hi the source: translated
        var same = TranslationPlanner.Plan(["description"], ["hi"], "hi", null, overwrite: true, _ => value);
        Assert.Contains("is the source language", Assert.Single(same).SkipReason);
        var missing = TranslationPlanner.Plan(["description"], ["ta"], "fr", null, overwrite: false, _ => value);
        Assert.Contains("no text in 'fr'", Assert.Single(missing).SkipReason);
        Assert.True(Assert.Single(missing).NoSource);
    }

    // ---- request validation --------------------------------------------------------------------------------------------

    private static Dictionary<string, string[]> Errors(TranslateInput input)
    {
        var result = new TranslateAssetValidator().Validate(new TranslateAssetCommand(Asset, input));
        return result.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
    }

    [Fact]
    public void A_good_request_passes()
    {
        Assert.Empty(Errors(new TranslateInput(["description"], ["hi", "ta"], "en", true)));
        Assert.Empty(Errors(new TranslateInput(["description"], ["pt-BR"])));
    }

    [Fact]
    public void Fields_and_languages_are_required()
    {
        var errors = Errors(new TranslateInput(null, null));
        Assert.Contains("at least one multilingual field", errors["fields"][0]);
        Assert.Contains("at least one language", errors["targetLanguages"][0]);
        Assert.True(Errors(new TranslateInput([], [])).ContainsKey("fields"));
    }

    [Fact]
    public void At_most_twenty_fields_and_ten_languages()
    {
        var twentyOne = Enumerable.Range(0, 21).Select(i => $"f{i}").ToList();
        var twenty = twentyOne.Take(20).ToList();
        Assert.Contains("At most 20", Errors(new TranslateInput(twentyOne, ["hi"]))["fields"][0]);
        Assert.Empty(Errors(new TranslateInput(twenty, ["hi"])));

        var elevenLanguages = new[] { "hi", "mr", "ta", "te", "bn", "gu", "kn", "ml", "pa", "fr", "de" };
        Assert.Contains("At most 10", Errors(new TranslateInput(["description"], elevenLanguages))["targetLanguages"][0]);
        Assert.Empty(Errors(new TranslateInput(["description"], elevenLanguages.Take(10).ToList())));
        // the same language written twice is one language
        Assert.Empty(Errors(new TranslateInput(["description"], [.. elevenLanguages.Take(10), "HI", "hi"])));
    }

    [Theory]
    [InlineData("h")]
    [InlineData("hi hi")]
    [InlineData("")]
    [InlineData("12")]
    public void A_target_or_source_that_is_not_a_language_code_is_refused(string code)
    {
        Assert.True(Errors(new TranslateInput(["description"], [code])).ContainsKey("targetLanguages"));
        Assert.True(Errors(new TranslateInput(["description"], ["hi"], code)).ContainsKey("sourceLanguage"));
    }

    [Fact]
    public void A_blank_field_name_is_refused()
    {
        Assert.True(Errors(new TranslateInput(["description", " "], ["hi"])).ContainsKey("fields"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("translation", true)]
    [InlineData("tag", false)]
    [InlineData("", false)]
    public void The_review_queue_only_knows_translations(string? kind, bool valid) =>
        Assert.Equal(valid, new ListAiReviewsValidator().Validate(new ListAiReviewsQuery(kind, null, null)).IsValid);

    // ---- the glossary --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_locked_term_needs_only_its_text()
    {
        Assert.Empty(GlossaryRules.Validate(new GlossaryTermInput("Acme Roadster X", "locked", null, null)));
        Assert.Empty(GlossaryRules.Validate(new GlossaryTermInput(" Acme Roadster X ", "locked", new Dictionary<string, string> { ["hi"] = "ignored" }, "Model name")));
    }

    [Fact]
    public void A_mapped_term_needs_at_least_one_translation_with_valid_languages_and_text()
    {
        Assert.Contains("at least one translation", GlossaryRules.Validate(new GlossaryTermInput("Acme Pulse", "mapped", null, null))["translations"][0]);
        Assert.Contains("at least one translation", GlossaryRules.Validate(new GlossaryTermInput("Acme Pulse", "mapped", [], null))["translations"][0]);
        Assert.Empty(GlossaryRules.Validate(new GlossaryTermInput("Acme Pulse", "mapped", new Dictionary<string, string> { ["HI"] = "एक्मे पल्स" }, null)));
        Assert.Contains("not a language code", GlossaryRules.Validate(new GlossaryTermInput("Acme Pulse", "mapped", new Dictionary<string, string> { ["x"] = "y" }, null))["translations"][0]);
        Assert.Contains("1 to 200", GlossaryRules.Validate(new GlossaryTermInput("Acme Pulse", "mapped", new Dictionary<string, string> { ["hi"] = " " }, null))["translations"][0]);
        Assert.Contains("appears twice", string.Join(' ', GlossaryRules.Validate(new GlossaryTermInput("Acme Pulse", "mapped", new Dictionary<string, string> { ["hi"] = "a", ["HI"] = "b" }, null))["translations"]));
    }

    [Theory]
    [InlineData("", "locked", "term")]
    [InlineData("   ", "locked", "term")]
    [InlineData(null, "locked", "term")]
    [InlineData("Acme\nX", "locked", "term")]
    [InlineData("Acme ⟦G1⟧", "locked", "term")]
    [InlineData("Acme X", "frozen", "mode")]
    [InlineData("Acme X", null, "mode")]
    public void Bad_terms_and_modes_are_refused_by_field(string? term, string? mode, string field) =>
        Assert.True(GlossaryRules.Validate(new GlossaryTermInput(term, mode, null, null)).ContainsKey(field));

    [Fact]
    public void Length_limits_apply_to_the_term_the_renderings_and_the_note()
    {
        Assert.True(GlossaryRules.Validate(new GlossaryTermInput(new string('a', 201), "locked", null, null)).ContainsKey("term"));
        Assert.Empty(GlossaryRules.Validate(new GlossaryTermInput(new string('a', 200), "locked", null, null)));
        Assert.True(GlossaryRules.Validate(new GlossaryTermInput("A", "locked", null, new string('n', 501))).ContainsKey("note"));
        Assert.True(GlossaryRules.Validate(new GlossaryTermInput("A", "mapped", new Dictionary<string, string> { ["hi"] = new string('x', 201) }, null)).ContainsKey("translations"));
    }

    [Fact]
    public void Normalising_lower_cases_languages_trims_renderings_and_drops_them_for_locked_terms()
    {
        var mapped = GlossaryRules.Normalize(new GlossaryTermInput("A", "mapped", new Dictionary<string, string> { ["HI"] = " पल्स " }, null));
        Assert.Equal(new Dictionary<string, string> { ["hi"] = "पल्स" }, mapped);
        Assert.Empty(GlossaryRules.Normalize(new GlossaryTermInput("A", "locked", new Dictionary<string, string> { ["hi"] = "x" }, null)));
    }

    [Fact]
    public void A_glossary_term_raises_an_event_for_each_change_and_forgets_translations_when_it_becomes_locked()
    {
        var term = GlossaryTerm.Create(Tenant, " Acme Pulse ", "mapped", new Dictionary<string, string> { ["HI"] = "पल्स" }, " model ", Now);
        Assert.Equal(("Acme Pulse", "model"), (term.Term, term.Note));
        Assert.Equal("पल्स", term.Translations["hi"]);
        Assert.IsType<GlossaryTermCreated>(Assert.Single(term.PullEvents()));

        term.Update("Acme Pulse", "locked", new Dictionary<string, string> { ["hi"] = "पल्स" }, null, Now.AddMinutes(1));
        Assert.Empty(term.Translations);
        Assert.Equal(Now.AddMinutes(1), term.UpdatedAt);
        var updated = Assert.IsType<GlossaryTermUpdated>(Assert.Single(term.PullEvents()));
        Assert.Equal(("glossary.term.updated", "glossary_term", term.Id), (updated.Name, updated.EntityType, updated.EntityId));

        term.MarkDeleted();
        Assert.Equal("glossary.term.deleted", Assert.Single(term.PullEvents()).Name);
        Assert.Equal(new GlossaryEntry("Acme Pulse", "locked", new Dictionary<string, string>()).Term, term.ToEntry().Term);
    }

    // ---- suggestions -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_translation_suggestion_carries_its_field_language_and_source_and_a_newer_one_replaces_it()
    {
        var s = AiSuggestion.CreateTranslation(Tenant, Asset, Guid.NewGuid(), "description", "hi", "en", TranslationRules.Hash("Hello"), "नमस्ते", "gpt-test", Now);
        Assert.True(s.IsTranslation);
        Assert.True(s.IsPending);
        Assert.Equal(("description", "hi", "en", AiSuggestionKinds.Translation), (s.Field, s.Language, s.SourceLanguage, s.Kind));

        var newVersion = Guid.NewGuid();
        s.Supersede(newVersion, "fr", TranslationRules.Hash("Salut"), "नया", "gpt-next", Now.AddHours(1));
        Assert.Equal(("नया", "fr", newVersion, Now.AddHours(1)), (s.Value, s.SourceLanguage, s.VersionId, s.CreatedAt));

        s.AcceptTranslation("नया (संपादित)", Guid.NewGuid(), Now.AddHours(2));
        Assert.Equal((AiSuggestionStatuses.Accepted, "नया (संपादित)"), (s.Status, s.Value));
    }

    [Fact]
    public void The_translation_kind_is_known_and_tags_and_languages_have_no_field()
    {
        Assert.True(AiSuggestionKinds.IsKnown("translation"));
        var tag = AiSuggestion.Create(Tenant, Asset, Guid.NewGuid(), AiSuggestionKinds.Tag, "hero", 0.9f, "m", Now);
        Assert.Equal((null, null, null, null), (tag.Field, tag.Language, tag.SourceLanguage, tag.SourceHash));
        Assert.False(tag.IsTranslation);
    }

    // ---- summaries on the extract and settings ------------------------------------------------------------------------------

    [Fact]
    public void A_summary_is_stored_with_its_language_and_model_and_blank_clears_it()
    {
        var x = AssetExtract.Create(Tenant, Asset, Guid.NewGuid(), Now);
        x.SetOcr("some text", "en", "ocr-model", Now);
        x.SetSummary("  A short summary.\0 ", "en", "extractive-v1", Now.AddMinutes(1));
        Assert.Equal(("A short summary.", "en", Now.AddMinutes(1)), (x.Summary, x.SummaryLanguage, x.SummaryAt));
        Assert.Equal(("ocr-model", "extractive-v1"), (x.Model["ocr"], x.Model["summary"]));
        x.SetSummary("   ", "en", "extractive-v1", Now.AddMinutes(2));
        Assert.Equal((null, null, null), (x.Summary, x.SummaryLanguage, x.SummaryAt));
        Assert.Equal("some text", x.OcrText);
    }

    [Fact]
    public void Summaries_default_to_on_including_for_settings_stored_before_the_feature_existed()
    {
        Assert.True(new AiFeatureFlags().Summaries);
        var stored = JsonSerializer.Deserialize<AiFeatureFlags>("{\"tags\":false,\"ocr\":true,\"transcribe\":true,\"embeddings\":false}", Web)!;
        Assert.Equal((false, true), (stored.Tags, stored.Summaries));
        var off = new AiFeatureFlags { Summaries = false };
        Assert.False(off.Copy().Summaries);
        var dto = JsonSerializer.Deserialize<AiFeaturesDto>("{\"tags\":true,\"ocr\":true,\"transcribe\":true,\"embeddings\":true}", Web)!;
        Assert.True(dto.Summaries);
    }

    [Fact]
    public void A_run_records_a_manual_summary_step_without_changing_its_status()
    {
        var run = AiRun.Create(Tenant, Asset, Guid.NewGuid(), Now);
        run.Start(Now);
        run.Succeed([new AiStep { Name = "ocr" }, new AiStep { Name = "summary", Status = AiStepStatuses.Skipped }], Now);
        run.UpsertStep(new AiStep { Name = "summary", Status = AiStepStatuses.Ok, Detail = "written on request" });
        Assert.Equal(AiRunStatuses.Succeeded, run.Status);
        Assert.Equal(["ocr", "summary"], run.Steps.Select(s => s.Name));
        Assert.Equal(AiStepStatuses.Ok, run.Steps.Single(s => s.Name == "summary").Status);
    }

    [Fact]
    public void The_new_options_are_read_from_the_environment_and_clamped()
    {
        var o = AiOptions.Read(k => k switch { "DAM_AI_SUMMARY_WORDS" => "200", "DAM_AI_CHAT_TIMEOUT_SECONDS" => "300", _ => null });
        Assert.Equal((200, TimeSpan.FromSeconds(300)), (o.SummaryWords, o.ChatTimeout));
        var defaults = AiOptions.Read(_ => null);
        Assert.Equal((120, TimeSpan.FromSeconds(90)), (defaults.SummaryWords, defaults.ChatTimeout));
        Assert.Equal(500, AiOptions.Read(k => k == "DAM_AI_SUMMARY_WORDS" ? "99999" : null).SummaryWords);
        Assert.Equal(20, AiOptions.Read(k => k == "DAM_AI_SUMMARY_WORDS" ? "1" : null).SummaryWords);
    }

    // ---- permission, catalogue, notification -----------------------------------------------------------------------------------

    [Fact]
    public void Managing_the_glossary_is_an_admin_permission_and_reading_it_needs_only_asset_access()
    {
        Assert.True(Permissions.IsKnown(Permissions.GlossaryManage));
        Assert.Equal("glossary.manage", Permissions.GlossaryManage);
        Assert.Contains(Permissions.GlossaryManage, BuiltInRoles.Find(BuiltInRoles.Admin)!.Permissions);
        Assert.DoesNotContain(BuiltInRoles.All.Where(r => r.Name != BuiltInRoles.Admin), r => r.Permissions.Contains(Permissions.GlossaryManage));
        Assert.Equal(Permissions.AssetsRead, new ListGlossaryQuery(null, null, null).Permission);
        Assert.Equal(Permissions.GlossaryManage, new CreateGlossaryTermCommand(new GlossaryTermInput("A", "locked", null, null)).Permission);
        Assert.Equal(Permissions.GlossaryManage, new UpdateGlossaryTermCommand(Guid.NewGuid(), new GlossaryTermInput("A", "locked", null, null)).Permission);
        Assert.Equal(Permissions.GlossaryManage, new DeleteGlossaryTermCommand(Guid.NewGuid()).Permission);
    }

    [Fact]
    public void Translations_ready_is_a_public_event_without_text_and_the_requester_hears_about_it_in_app()
    {
        var info = EventCatalogue.All.Single(e => e.Name == AssetTranslationReady.EventName);
        Assert.Equal(("asset", false), (info.Category, info.Derived));
        Assert.Contains(AssetTranslationReady.EventName, EventCatalogue.SourceEvents);
        Assert.DoesNotContain(AssetTranslationRequested.EventName, EventCatalogue.SourceEvents);           // the request itself is internal

        var category = NotificationCategories.Find(NotificationCategories.Translation)!;
        Assert.Equal((true, false), (category.DefaultInApp, category.DefaultEmail));
    }

    [Fact]
    public void The_notification_says_how_many_wait_for_review_and_how_many_failed()
    {
        var id = Guid.NewGuid();
        var ready = NotificationRules.TranslationReady(id, "Roadster hero", "succeeded", 3, 1, 0);
        Assert.Equal(("asset.translation.ready", "translation"), (ready.Type, ready.Category));
        Assert.Contains("Translations ready", ready.Title);
        Assert.Contains("3 translations", ready.Body);
        Assert.Contains("are waiting", ready.Body);
        Assert.Equal(NotificationRules.AssetPath(id), ready.LinkPath);

        var one = NotificationRules.TranslationReady(id, "Roadster hero", "succeeded", 1, 0, 0);
        Assert.Contains("1 translation of", one.Body);
        Assert.Contains("is waiting", one.Body);

        var partial = NotificationRules.TranslationReady(id, "Roadster hero", "partial", 2, 0, 1);
        Assert.Contains("1 could not be made", partial.Body);

        var failed = NotificationRules.TranslationReady(id, "Roadster hero", "failed", 0, 0, 2);
        Assert.StartsWith("Translation failed", failed.Title);
        var jobFailed = NotificationRules.TranslationReady(id, "Roadster hero", "failed", 0, 0, 0);
        Assert.StartsWith("Translation failed", jobFailed.Title);
        var nothing = NotificationRules.TranslationReady(id, "Roadster hero", "succeeded", 0, 4, 0);
        Assert.StartsWith("Nothing to translate", nothing.Title);
    }

    [Fact]
    public void A_chat_channel_hears_translations_ready_in_plain_words()
    {
        var (title, body) = NotificationRules.Describe("asset.translation.ready", "Roadster hero", "2 translated");
        Assert.Equal(("Translations ready: Roadster hero", "2 translated"), (title, body));
    }

    // ---- tenant settings ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tenant_language_settings_are_read_leniently()
    {
        var t = TenantLanguages.Parse("{\"languages\":[\"EN\",\"hi\"],\"defaultLanguage\":\"HI\"}");
        Assert.Equal(("hi", true, false), (t.Default, t.Allows("en"), t.Allows("ta")));
        Assert.True(TenantLanguages.Parse("{\"languages\":[]}").Allows("anything"));
        Assert.True(TenantLanguages.Parse("not json").Allows("anything"));
        Assert.True(TenantLanguages.Parse(null).Allows("anything"));
        Assert.Null(TenantLanguages.Parse("{\"defaultLanguage\":\"!!\"}").Default);
    }

    // ---- providers --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_self_hosted_provider_summarises_offline_and_cannot_yet_translate()
    {
        var p = new SelfHostedProvider(new AiOptions { SidecarUrl = "http://unreachable.invalid" }, NullLogger<SelfHostedProvider>.Instance);
        Assert.True(p.Capabilities.Summarise);
        Assert.False(p.Capabilities.Translate);
        var r = await p.SummariseAsync("The Acme showroom opens at nine. It closes at six.", new SummaryOptions(MaxWords: 50), default);
        Assert.Equal(("extractive-v1", "The Acme showroom opens at nine. It closes at six."), (r.Model, r.Text));
        var ex = await Assert.ThrowsAsync<AiNotSupportedException>(() => p.TranslateAsync("a", "en", "hi", Glossary.Empty, default));
        Assert.Equal("self-hosted", ex.Provider);
        Assert.Equal("", (await p.SummariseAsync("   ", new SummaryOptions(), default)).Text);
    }
}
