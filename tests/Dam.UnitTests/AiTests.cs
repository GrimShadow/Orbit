using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Content;
using Dam.Infrastructure.Ai;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dam.UnitTests;

public sealed class RankFusionTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    [Fact]
    public void An_item_scores_the_sum_of_one_over_k_plus_rank_across_the_lists_it_is_in()
    {
        var fused = RankFusion.Fuse([new("lexical", [A, B]), new("image", [B, C])], k: 60);
        var b = fused.Single(f => f.Id == B);
        Assert.Equal(1.0 / 62 + 1.0 / 61, b.Score, 12);               // rank 2 in the first list, rank 1 in the second
        Assert.Equal(1.0 / 61, fused.Single(f => f.Id == A).Score, 12);
        Assert.Equal(1.0 / 62, fused.Single(f => f.Id == C).Score, 12);
        Assert.Equal([B, A, C], fused.Select(f => f.Id));
    }

    [Fact]
    public void An_item_second_in_two_lists_beats_one_first_in_only_one()
    {
        var fused = RankFusion.Fuse([new("lexical", [A, C]), new("image", [B, C])]);
        Assert.Equal(C, fused[0].Id);
        Assert.Equal(["lexical", "image"], fused[0].Legs);
        Assert.Equal(2, fused[0].BestRank);
    }

    [Fact]
    public void A_missing_or_empty_list_simply_contributes_nothing()
    {
        var fused = RankFusion.Fuse([new("lexical", [A, B]), new("image", []), new("meaning", [])]);
        Assert.Equal([A, B], fused.Select(f => f.Id));
        Assert.Empty(RankFusion.Fuse([]));
        Assert.Empty(RankFusion.Fuse([new("x", [])]));
    }

    [Fact]
    public void Ties_break_by_best_rank_and_then_by_id_so_the_order_is_deterministic()
    {
        // A is first in one list only, B first in the other only: equal scores and equal best rank, so the id decides.
        var fused = RankFusion.Fuse([new("one", [B]), new("two", [A])]);
        Assert.Equal([A, B], fused.Select(f => f.Id));

        // Same score, different best rank: D is 2nd in both lists' sum? Construct: X rank 1 + nothing, Y rank 2 in two lists beats equal totals only by sum, so use equal sums.
        var tie = RankFusion.Fuse([new("one", [C, D]), new("two", [D, C])]);
        Assert.Equal(tie[0].Score, tie[1].Score, 12);
        Assert.Equal([C, D], tie.Select(f => f.Id));   // both best rank 1: the id decides
    }

    [Fact]
    public void A_larger_k_flattens_the_difference_between_ranks()
    {
        double Gap(int k) { var f = RankFusion.Fuse([new("l", [A, B])], k); return f[0].Score - f[1].Score; }
        Assert.True(Gap(1) > Gap(60));
        Assert.True(Gap(60) > Gap(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankFusion.Fuse([new("l", [A])], 0));
    }

    [Fact]
    public void A_repeated_id_in_one_list_counts_once_at_its_best_rank()
    {
        var fused = RankFusion.Fuse([new("l", [A, A, B])], 60);
        Assert.Equal(1.0 / 61, fused.Single(f => f.Id == A).Score, 12);
        Assert.Equal(1.0 / 62, fused.Single(f => f.Id == B).Score, 12);   // B is second distinct item
    }
}

public sealed class AiRulesTests
{
    // ---- tag filtering

    [Fact]
    public void Tag_candidates_are_normalised_filtered_by_threshold_deduplicated_and_never_repeat_what_exists()
    {
        var picked = TagCandidates.Select(
            [("  Outdoor ", 0.9f), ("outdoor", 0.8f), ("Sky", 0.7f), ("blurry", 0.05f), ("#Hero.", 0.6f), ("", 0.9f), ("x", float.NaN)],
            existing: ["sky"], topK: 10, threshold: 0.2f);
        Assert.Equal(["outdoor", "hero"], picked.Select(p => p.Label));
        Assert.Equal(0.9f, picked[0].Score);
    }

    [Fact]
    public void Tag_candidates_are_limited_to_the_top_k_best_scoring()
    {
        var picked = TagCandidates.Select([("a", 0.5f), ("b", 0.9f), ("c", 0.7f)], [], topK: 2, threshold: 0f);
        Assert.Equal(["b", "c"], picked.Select(p => p.Label));
    }

    [Fact]
    public void Overlong_tags_are_dropped_and_scores_are_clamped()
    {
        var picked = TagCandidates.Select([(new string('x', 51), 0.9f), ("ok", 1.7f)], [], 5, 0f);
        Assert.Equal([("ok", 1f)], picked);
    }

    // ---- chunking

    [Fact]
    public void Text_is_chunked_on_sentence_boundaries_within_the_limit()
    {
        var text = "One short sentence. Another sentence follows here. A third one closes the thought.";
        var chunks = TextChunker.Chunk(text, maxChars: 45);
        Assert.All(chunks, c => Assert.True(c.Length <= 45, c));
        Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
        Assert.True(chunks.Count >= 2);
        Assert.Equal("One short sentence.", chunks[0]);
    }

    [Fact]
    public void Chunking_stops_at_the_chunk_limit_and_copes_with_empty_input_and_one_giant_line()
    {
        Assert.Empty(TextChunker.Chunk("   "));
        Assert.Empty(TextChunker.Chunk((string?)null));
        var many = string.Join(' ', Enumerable.Repeat("Hello there friend.", 40));
        Assert.Equal(3, TextChunker.Chunk(many, 60, maxChunks: 3).Count);
        var giant = TextChunker.Chunk(new string('a', 2500), 1000);
        Assert.Equal(3, giant.Count);
        Assert.All(giant, c => Assert.True(c.Length <= 1000));
    }

    [Fact]
    public void A_transcript_is_chunked_from_its_segments()
    {
        var chunks = TextChunker.Chunk([new TranscriptSegment(0, 1000, "Welcome to the showroom."), new TranscriptSegment(1000, 2000, "Our roadster has a panoramic roof.")], 40);
        Assert.Equal(["Welcome to the showroom.", "Our roadster has a panoramic roof."], chunks);
    }

    // ---- vectors

    [Fact]
    public void The_mean_of_vectors_is_unit_length_and_between_them()
    {
        var mean = VectorMath.NormalizedMean([[1f, 0f, 0f], [0f, 1f, 0f]])!;
        Assert.Equal(1.0, Math.Sqrt(mean.Sum(x => (double)x * x)), 5);
        Assert.Equal(mean[0], mean[1], 5);
        Assert.Null(VectorMath.NormalizedMean([]));
        Assert.Throws<ArgumentException>(() => VectorMath.NormalizedMean([[1f], [1f, 2f]]));
    }

    [Fact]
    public void Normalising_a_zero_vector_gives_zeros_and_cosine_of_orthogonal_vectors_is_zero()
    {
        Assert.All(VectorMath.Normalize([0f, 0f]), x => Assert.Equal(0f, x));
        Assert.Equal(0, VectorMath.Cosine([1f, 0f], [0f, 1f]), 9);
        Assert.Equal(1, VectorMath.Cosine([2f, 0f], [5f, 0f]), 9);
        Assert.Equal(0.5, VectorMath.CosineFromKnnScore(0.75), 9);   // OpenSearch scores a cosine hit (1 + cos) / 2
    }

    [Fact]
    public void Embeddings_survive_the_round_trip_as_little_endian_float32()
    {
        float[] v = [0.5f, -1.25f, 3.0e-5f, float.MaxValue, 0f];
        var bytes = EmbeddingCodec.ToBytes(v);
        Assert.Equal(v.Length * 4, bytes.Length);
        Assert.Equal(v, EmbeddingCodec.FromBytes(bytes));
        Assert.Equal([0x00, 0x00, 0x00, 0x3F], bytes[..4]);   // 0.5f is 0x3F000000, least significant byte first
        Assert.Throws<ArgumentException>(() => EmbeddingCodec.FromBytes(new byte[5]));

        var row = AssetEmbedding.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmbeddingKinds.Text, "m", v, DateTimeOffset.UtcNow);
        Assert.Equal(5, row.Dim);
        Assert.Equal(v, row.ToVector());
    }

    // ---- audio

    [Theory]
    [InlineData(60, 3600, 32)]
    [InlineData(100, 6000, 32)]
    [InlineData(120, 7200, 24)]       // beyond 100 minutes the bitrate drops to stay under the upload limit
    [InlineData(133, 7980, 24)]
    public void Audio_is_capped_and_encoded_to_fit_the_upload_limit(int minutes, int expectedSeconds, int expectedKbps)
    {
        var plan = AudioPlan.For(minutes);
        Assert.Equal((expectedSeconds, expectedKbps), (plan.Seconds, plan.BitrateKbps));
        Assert.True(plan.Seconds * plan.BitrateKbps * 125L <= AudioPlan.MaxUploadBytes);
    }

    [Fact]
    public void Very_long_recordings_are_cut_to_what_fits_and_nonsense_is_clamped()
    {
        var plan = AudioPlan.For(10_000);
        Assert.True(plan.Seconds * plan.BitrateKbps * 125L <= AudioPlan.MaxUploadBytes);
        Assert.True(plan.Seconds < 10_000 * 60);
        Assert.Equal(60, AudioPlan.For(0).Seconds);   // at least a minute
    }

    // ---- languages

    [Theory]
    [InlineData("en", "en")]
    [InlineData("English", "en")]
    [InlineData("HINDI", "hi")]
    [InlineData("pt-BR", "pt")]
    [InlineData("deu", "deu")]
    [InlineData("klingon-ish language", null)]
    [InlineData("", null)]
    public void Languages_are_normalised_to_short_codes(string input, string? expected) => Assert.Equal(expected, LanguageCodes.Normalize(input));

    // ---- run record

    [Fact]
    public void A_run_moves_through_its_states_and_never_raises_audited_events()
    {
        var now = DateTimeOffset.UtcNow;
        var run = AiRun.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        Assert.True(run.IsActive);
        run.Start(now);
        Assert.Equal(AiRunStatuses.Running, run.Status);
        run.NoteRetry("provider down", now);
        Assert.Equal(AiRunStatuses.Pending, run.Status);
        Assert.Empty(run.DomainEvents);
        run.Start(now);
        Assert.Equal(2, run.Attempts);
        run.Succeed([new AiStep { Name = "tags" }], now);
        Assert.Equal(AiRunStatuses.Succeeded, run.Status);
        Assert.Empty(run.PullEvents());     // the run record is the record; the index is told through the outbox by the pipeline
        run.Queue();
        Assert.Equal(AiRunStatuses.Pending, run.Status);
        Assert.Empty(run.Steps);
        run.Fail([], new string('x', 5000), now);
        Assert.Equal(1000, run.Error.Length);
        Assert.Empty(run.PullEvents());
    }

    [Fact]
    public void A_suggestion_clamps_its_confidence_and_records_who_decided()
    {
        var s = AiSuggestion.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiSuggestionKinds.Tag, "hero", 7f, "m", DateTimeOffset.UtcNow);
        Assert.Equal(1f, s.Confidence);
        Assert.True(s.IsPending);
        var me = Guid.NewGuid();
        s.Accept(me, DateTimeOffset.UtcNow);
        Assert.False(s.IsPending);
        Assert.Equal(me, s.DecidedBy);
    }

    [Fact]
    public void Adding_tags_normalises_respects_the_limit_and_announces_a_metadata_change_only_when_something_was_added()
    {
        var asset = Asset.CreateShell(Guid.NewGuid(), null, "image", "x", null, DateTimeOffset.UtcNow);
        asset.PullEvents();
        asset.SetTags(["hero"], DateTimeOffset.UtcNow);
        Assert.Equal(["outdoor"], asset.AddTags([" Outdoor ", "hero"], 50, DateTimeOffset.UtcNow));
        Assert.Contains(asset.PullEvents(), e => e is AssetMetadataUpdated);
        Assert.Equal(["hero", "outdoor"], asset.Tags);

        Assert.Empty(asset.AddTags(["hero"], 50, DateTimeOffset.UtcNow));
        Assert.Empty(asset.PullEvents());
        Assert.Empty(asset.AddTags(["more"], 2, DateTimeOffset.UtcNow));   // already at the limit
        Assert.Equal(["a"], asset.AddTags(["a", "b"], 3, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_ai_manage_permission_is_known_and_only_admin_holds_it()
    {
        Assert.True(Permissions.IsKnown(Permissions.AiManage));
        Assert.Equal("ai.manage", Permissions.AiManage);
        Assert.Contains(Permissions.AiManage, BuiltInRoles.Find(BuiltInRoles.Admin)!.Permissions);
        Assert.DoesNotContain(BuiltInRoles.All.Where(r => r.Name != BuiltInRoles.Admin), r => r.Permissions.Contains(Permissions.AiManage));
    }
}

public sealed class AiOptionsTests
{
    private static AiOptions Read(params (string, string?)[] values) =>
        AiOptions.Read(k => values.Where(v => v.Item1 == k).Select(v => v.Item2).FirstOrDefault());

    [Fact]
    public void Defaults_are_no_provider_384_text_and_512_image_dimensions()
    {
        var o = Read();
        Assert.Equal("none", o.DefaultProvider);
        Assert.Equal((384, 512), (o.TextDim, o.ImageDim));
        Assert.Equal((60, 5, 200, 60), (o.MaxAudioMinutes, o.MaxOcrPages, o.SemanticDepth, o.RrfK));
        Assert.Null(o.TagThreshold);                                                      // each provider applies its own
        Assert.Equal((0.22, 0.80, 0.30, 0.04), (o.ImageFloor, o.TextFloor("self-hosted"), o.TextFloor("openai-compatible"), o.KnnMargin));
        Assert.False(o.SelfHostedConfigured);
        Assert.False(o.OpenAiConfigured);
        Assert.True(o.IsConfigured("none"));
    }

    [Fact]
    public void Settings_are_read_from_the_environment_style_keys_and_clamped_to_sane_bounds()
    {
        var o = Read(("DAM_AI_PROVIDER", "OpenAI"), ("DAM_AI_BASE_URL", "https://example.test/v1/"), ("DAM_AI_API_KEY", " k "), ("DAM_AI_TEXT_DIM", "1536"),
            ("DAM_AI_IMAGE_DIM", "banana"), ("DAM_AI_MAX_AUDIO_MINUTES", "100000"), ("DAM_AI_RRF_K", "0"), ("DAM_AI_SEMANTIC_DEPTH", "50"), ("DAM_AI_TEXT_MIN_COSINE", "0.55"), ("DAM_AI_IMAGE_MIN_COSINE", "x"), ("DAM_AI_TAG_THRESHOLD", "0.4"));
        Assert.Equal((0.55, 0.55, 0.22, 0.4f), (o.TextFloor("self-hosted"), o.TextFloor("openai-compatible"), o.ImageFloor, o.TagThreshold));
        Assert.Equal("openai-compatible", o.DefaultProvider);
        Assert.Equal("https://example.test/v1", o.BaseUrl);
        Assert.Equal("k", o.ApiKey);
        Assert.Equal(1536, o.TextDim);
        Assert.Equal(512, o.ImageDim);
        Assert.Equal(AudioPlan.HardCapMinutes, o.MaxAudioMinutes);
        Assert.Equal(1, o.RrfK);
        Assert.Equal(50, o.SemanticDepth);
        Assert.True(o.OpenAiConfigured);
    }

    [Fact]
    public void One_model_named_for_chat_or_vision_serves_both_unless_the_other_is_named_too()
    {
        var chatOnly = Read(("DAM_AI_CHAT_MODEL", "m-chat"));
        Assert.Equal(("m-chat", "m-chat"), (chatOnly.VisionModel, chatOnly.ChatModel));
        var visionOnly = Read(("DAM_AI_VISION_MODEL", "m-vision"));
        Assert.Equal(("m-vision", "m-vision"), (visionOnly.VisionModel, visionOnly.ChatModel));
        var both = Read(("DAM_AI_CHAT_MODEL", "m-chat"), ("DAM_AI_VISION_MODEL", "m-vision"), ("DAM_AI_EMBEDDING_MODEL", "m-emb"), ("DAM_AI_TRANSCRIPTION_MODEL", "m-tr"));
        Assert.Equal(("m-vision", "m-chat", "m-emb", "m-tr"), (both.VisionModel, both.ChatModel, both.EmbeddingModel, both.TranscriptionModel));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://acme.openai.azure.com/openai/deployments/{model}", true)]
    [InlineData("http://localhost:8000/v1", false)]
    [InlineData("http://127.0.0.1:11434/v1", false)]
    [InlineData("http://10.1.2.3/v1", false)]
    [InlineData("http://192.168.1.20:8000/v1", false)]
    [InlineData("http://vllm/v1", false)]
    [InlineData("http://llm.corp.internal/v1", false)]
    [InlineData("http://172.32.0.1/v1", true)]
    public void A_server_on_this_machine_or_a_private_network_does_not_send_data_off_site(string url, bool offSite)
    {
        var o = new AiOptions { BaseUrl = url };
        Assert.Equal(offSite, o.SendsDataOffSite("openai-compatible"));
        Assert.False(o.SendsDataOffSite("self-hosted"));
        Assert.False(o.SendsDataOffSite("none"));
    }

    [Fact]
    public void Provider_kinds_are_parsed_leniently_and_unknown_means_none()
    {
        Assert.Equal("self-hosted", AiProviderKinds.Parse("SelfHosted"));
        Assert.Equal("openai-compatible", AiProviderKinds.Parse("azure"));
        Assert.Equal("none", AiProviderKinds.Parse("gemini"));
        Assert.Equal("none", AiProviderKinds.Parse(null));
        Assert.True(AiProviderKinds.IsKnown("none"));
        Assert.False(AiProviderKinds.IsKnown("openai"));
    }
}

public sealed class AiProviderShapeTests
{
    [Fact]
    public void Capability_flags_tell_the_truth_for_each_provider()
    {
        var selfHosted = new SelfHostedProvider(new AiOptions { SidecarUrl = "http://x" }, NullLogger<SelfHostedProvider>.Instance);
        Assert.True(selfHosted.Capabilities is { Tags: true, Ocr: true, Transcribe: true, TextEmbeddings: true, ImageEmbeddings: true, DetectLanguage: true, Translate: false, Summarise: true });

        var openAi = new OpenAiCompatibleProvider(new AiOptions { BaseUrl = "http://x" }, NullLogger<OpenAiCompatibleProvider>.Instance);
        Assert.True(openAi.Capabilities is { Tags: true, Ocr: true, Transcribe: true, TextEmbeddings: true, ImageEmbeddings: false, DetectLanguage: true, Translate: true, Summarise: true });

        var noTranscription = new OpenAiCompatibleProvider(new AiOptions { BaseUrl = "http://x", TranscriptionModel = "" }, NullLogger<OpenAiCompatibleProvider>.Instance);
        Assert.False(noTranscription.Capabilities.Transcribe);

        Assert.Equal(AiCapabilities.None, new NullProvider().Capabilities);
    }

    [Fact]
    public async Task The_null_provider_and_unsupported_calls_throw_a_typed_not_supported_error()
    {
        var none = new NullProvider("off");
        await Assert.ThrowsAsync<AiNotSupportedException>(() => none.EmbedTextAsync("x", new TextEmbedOptions(), default));
        await Assert.ThrowsAsync<AiNotSupportedException>(() => none.TagImageAsync(Stream.Null, new TagOptions(), default));
        Assert.Equal("disabled", (await none.CheckHealthAsync(default)).Status);

        var selfHosted = new SelfHostedProvider(new AiOptions { SidecarUrl = "http://x" }, NullLogger<SelfHostedProvider>.Instance);
        var ex = await Assert.ThrowsAsync<AiNotSupportedException>(() => selfHosted.TranslateAsync("a", "en", "hi", Glossary.Empty, default));
        Assert.Equal("self-hosted", ex.Provider);
        // Summaries are made locally (extractive), so the self-hosted provider does summarise; translation needs a model it does not have yet.
        Assert.Equal(ExtractiveSummary.ModelName, (await selfHosted.SummariseAsync("A short note.", new SummaryOptions(), default)).Model);

        var openAi = new OpenAiCompatibleProvider(new AiOptions { BaseUrl = "http://x" }, NullLogger<OpenAiCompatibleProvider>.Instance);
        await Assert.ThrowsAsync<AiNotSupportedException>(() => openAi.EmbedImageAsync(Stream.Null, default));
        await Assert.ThrowsAsync<AiNotSupportedException>(() => openAi.EmbedTextAsync("x", new TextEmbedOptions(TextEmbeddingKind.Clip), default));
    }

    [Fact]
    public void Provider_selection_returns_the_right_instance_for_each_kind()
    {
        var options = new AiOptions { SidecarUrl = "http://x", BaseUrl = "http://y" };
        var s = new SelfHostedProvider(options, NullLogger<SelfHostedProvider>.Instance);
        var o = new OpenAiCompatibleProvider(options, NullLogger<OpenAiCompatibleProvider>.Instance);
        var providers = new AiProviders(options, s, o);
        Assert.Same(s, providers.Get("self-hosted"));
        Assert.Same(o, providers.Get("openai-compatible"));
        Assert.Equal("none", providers.Get("none").Kind);
        Assert.Equal("none", providers.Get("anything else").Kind);
    }

    [Fact]
    public void The_cool_down_breaker_opens_after_the_threshold_and_closes_after_the_pause()
    {
        var time = new ManualTime();
        var breaker = new CoolDownBreaker(3, TimeSpan.FromSeconds(30), time);
        breaker.Failure(); breaker.Failure();
        Assert.False(breaker.IsOpen);
        breaker.Failure();
        Assert.True(breaker.IsOpen);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(breaker.IsOpen);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(breaker.IsOpen);
        breaker.Failure();                      // the trial call after the pause fails again: open at once
        Assert.True(breaker.IsOpen);
        time.Advance(TimeSpan.FromSeconds(31));
        breaker.Success();
        breaker.Failure();
        Assert.False(breaker.IsOpen);           // a success resets the count
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

public sealed class AiMappingTests
{
    private static List<FieldDefinition> NoFields => [];

    private static Dictionary<string, object?> Props(Dictionary<string, object?> body) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)body["mappings"]!)["properties"]!;

    [Fact]
    public void The_vector_fields_use_the_configured_dimensions_on_the_lucene_engine_with_cosine_similarity()
    {
        var props = Props(IndexMappingBuilder.BuildIndexBody(NoFields, new VectorDimensions(768, 1024)));
        var text = (Dictionary<string, object?>)props["vector_text"]!;
        var image = (Dictionary<string, object?>)props["vector_image"]!;
        Assert.Equal(768, text["dimension"]);
        Assert.Equal(1024, image["dimension"]);
        var method = (Dictionary<string, object?>)text["method"]!;
        Assert.Equal("lucene", method["engine"]);
        Assert.Equal("cosinesimil", method["space_type"]);
        Assert.Equal("knn_vector", text["type"]);
    }

    [Fact]
    public void Without_dimensions_the_defaults_are_384_text_and_512_image()
    {
        var props = Props(IndexMappingBuilder.BuildIndexBody(NoFields));
        Assert.Equal(384, ((Dictionary<string, object?>)props["vector_text"]!)["dimension"]);
        Assert.Equal(512, ((Dictionary<string, object?>)props["vector_image"]!)["dimension"]);
    }

    [Fact]
    public void Extracted_text_and_the_transcript_are_mapped_and_the_transcript_is_nested_with_start_times()
    {
        var props = Props(IndexMappingBuilder.BuildIndexBody(NoFields));
        Assert.Equal("text", ((Dictionary<string, object?>)props["ocr_text"]!)["type"]);
        Assert.Equal("text", ((Dictionary<string, object?>)props["transcript_text"]!)["type"]);
        var segments = (Dictionary<string, object?>)props["transcript_segments"]!;
        Assert.Equal("nested", segments["type"]);
        var inner = (Dictionary<string, object?>)segments["properties"]!;
        Assert.Equal(["end_ms", "start_ms", "text"], inner.Keys.Order());
        Assert.Equal(IndexMappingBuilder.BuildExtractProperties().Keys.Order(), ["ocr_text", "summary_text", "transcript_segments", "transcript_text"]);
    }

    [Fact]
    public void Ai_data_is_added_to_a_document_only_when_the_asset_has_it()
    {
        var asset = Asset.CreateShell(Guid.NewGuid(), null, "image", "Hero", null, DateTimeOffset.UtcNow);
        var empty = SearchDocumentBuilder.Build(asset, null, TenantSchemaIndex.Empty, TenantTaxonomyIndex.Empty, TenantCollectionIndex.Empty);
        Assert.DoesNotContain("vector_text", empty.Keys);
        Assert.DoesNotContain("ocr_text", empty.Keys);

        var ai = new AiIndexData("some text", "spoken words", [new TranscriptSegment(1500, 3000, "spoken words")], [0.5f, 0.5f], null);
        var doc = SearchDocumentBuilder.Build(asset, null, TenantSchemaIndex.Empty, TenantTaxonomyIndex.Empty, TenantCollectionIndex.Empty, ai);
        Assert.Equal("some text", doc["ocr_text"]);
        Assert.Equal("spoken words", doc["transcript_text"]);
        Assert.DoesNotContain("summary_text", doc.Keys);
        Assert.Equal(new[] { 0.5f, 0.5f }, (float[])doc["vector_text"]!);
        Assert.DoesNotContain("vector_image", doc.Keys);
        var segment = Assert.Single((List<Dictionary<string, object?>>)doc["transcript_segments"]!);
        Assert.Equal((1500L, "spoken words"), ((long)segment["start_ms"]!, (string)segment["text"]!));
    }
}
