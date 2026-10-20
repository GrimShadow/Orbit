using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Infrastructure.Ai;
using Dam.UnitTests.Support;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

/// <summary>Both AI providers against in-process stand-ins for the services they talk to (no containers needed): request shapes, response mapping, retries, secrets.</summary>
public sealed class SelfHostedProviderTests
{
    private static SelfHostedProvider Provider(FakeSidecar fake, Action<AiOptions>? tune = null, ILoggerProvider? logs = null)
    {
        var o = new AiOptions { SidecarUrl = fake.Url, SidecarToken = fake.RequiredToken, RetryBaseDelay = TimeSpan.FromMilliseconds(1), CoolDown = TimeSpan.FromMilliseconds(300) };
        tune?.Invoke(o);
        using var factory = LoggerFactory.Create(b => { if (logs is not null) b.AddProvider(logs); });
        return new SelfHostedProvider(o, factory.CreateLogger<SelfHostedProvider>());
    }

    private static MemoryStream Picture(byte r = 220, byte g = 30, byte b = 30) => new(TestImages.Solid(r, g, b));

    [Fact]
    public async Task Every_feature_maps_to_its_endpoint_and_back()
    {
        await using var fake = await FakeSidecar.StartAsync();
        fake.RequiredToken = "sidecar-secret";
        using var provider = Provider(fake);

        var health = await provider.CheckHealthAsync(default);
        Assert.Equal("ok", health.Status);
        Assert.Contains("clip-fake", health.Models!["clip"]);
        Assert.Contains("(not loaded)", health.Models["ocr"]);
        Assert.Contains("0.0-test", health.Detail);

        var tags = await provider.TagImageAsync(Picture(), new TagOptions(TopK: 7, Threshold: 0.3f, Labels: ["car", "dog"]), default);
        Assert.Equal("fake-sidecar-tagger", tags.Model);
        Assert.Contains(tags.Tags, t => t.Label == "Outdoor" && t.Score > 0.9f);
        var tagCall = fake.Calls.Single(c => c.Path == "/v1/tag");
        Assert.Equal("Bearer sidecar-secret", tagCall.Authorization);
        Assert.Contains("top_k=7", tagCall.Body);
        Assert.Contains("threshold=0.3", tagCall.Body);
        Assert.Contains("labels=[\"car\",\"dog\"]", tagCall.Body);

        var ocr = await provider.OcrAsync(Picture(), new OcrOptions("en"), default);
        Assert.Equal("Acme roadster showroom opening hours", ocr.Text);
        Assert.Equal("en", ocr.Language);
        Assert.NotEmpty(ocr.Lines);
        Assert.Contains("lang=en", fake.Calls.Single(c => c.Path == "/v1/ocr").Body);

        var image = await provider.EmbedImageAsync(Picture(), default);
        Assert.Equal(512, image.Dim);
        Assert.Equal(1.0, Math.Sqrt(image.Vector.Sum(x => (double)x * x)), 4);

        var clip = await provider.EmbedTextAsync("red suv on a beach", new TextEmbedOptions(TextEmbeddingKind.Clip, TextEmbeddingRole.Query), default);
        Assert.Equal(512, clip.Dim);
        var text = await provider.EmbedTextsAsync(["a", "b", "c"], new TextEmbedOptions(TextEmbeddingKind.Text, TextEmbeddingRole.Query), default);
        Assert.Equal(3, text.Vectors.Count);
        Assert.All(text.Vectors, v => Assert.Equal(384, v.Length));
        var bodies = fake.Calls.Where(c => c.Path == "/v1/embed/text").Select(c => JsonDocument.Parse(c.Body).RootElement).ToList();
        Assert.Equal("clip", bodies[0].GetProperty("kind").GetString());
        Assert.False(bodies[0].TryGetProperty("role", out _));            // role belongs to the text space only
        Assert.Equal(("text", "query"), (bodies[1].GetProperty("kind").GetString(), bodies[1].GetProperty("role").GetString()));

        await using var audio = new MemoryStream(TestImages.Wav(1));
        var transcript = await provider.TranscribeAsync(audio, new TranscribeOptions("en", 600), default);
        Assert.Equal(3, transcript.Segments.Count);
        Assert.Equal(new TranscriptSegment(2500, 6000, "Our new roadster has a panoramic roof"), transcript.Segments[1]);
        Assert.Equal(9000, transcript.DurationMs);
        Assert.Equal("en", transcript.Language);
        var transcribeCall = fake.Calls.Single(c => c.Path == "/v1/transcribe").Body;
        Assert.Contains("max_seconds=600", transcribeCall);
        Assert.Contains("lang=en", transcribeCall);

        var language = await provider.DetectLanguageAsync("hola amigos", default);
        Assert.Equal(("es", true), (language.Language, language.Confidence > 0.9f));
    }

    [Fact]
    public async Task A_busy_sidecar_is_waited_for_and_then_succeeds_without_buffering_the_upload_twice()
    {
        await using var fake = await FakeSidecar.StartAsync();
        fake.Fail503 = 2;
        using var provider = Provider(fake);
        var image = await provider.EmbedImageAsync(Picture(), default);   // a MemoryStream is seekable, so the upload is sent again
        Assert.Equal(512, image.Dim);
        Assert.Equal(3, fake.CallsTo("/v1/embed/image"));
    }

    [Fact]
    public async Task A_stream_that_cannot_be_rewound_is_tried_once()
    {
        await using var fake = await FakeSidecar.StartAsync();
        fake.Down = true;
        using var provider = Provider(fake);
        await using var forward = new ForwardOnlyStream(TestImages.Solid(1, 2, 3));
        await Assert.ThrowsAsync<AiUnavailableException>(() => provider.EmbedImageAsync(forward, default));
        Assert.Equal(1, fake.CallsTo("/v1/embed/image"));
    }

    [Fact]
    public async Task A_sidecar_that_stays_down_trips_the_cool_down_so_later_calls_fail_without_calling_it()
    {
        await using var fake = await FakeSidecar.StartAsync();
        fake.Down = true;
        using var provider = Provider(fake, o => { o.Retries = 2; o.FailureThreshold = 2; });

        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<AiUnavailableException>(() => provider.EmbedImageAsync(Picture(), default));
        var before = fake.Calls.Count;
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => provider.EmbedImageAsync(Picture(), default));
        Assert.Contains("waiting", ex.Message);
        Assert.Equal(before, fake.Calls.Count);   // failed at once, nothing sent

        await Task.Delay(400);                    // the pause ends: one trial call goes through
        fake.Down = false;
        Assert.Equal(512, (await provider.EmbedImageAsync(Picture(), default)).Dim);
    }

    [Fact]
    public async Task A_refused_input_is_not_retried_and_the_credentials_are_reported_distinctly()
    {
        await using var fake = await FakeSidecar.StartAsync();
        fake.RequiredToken = "right";
        using var wrong = Provider(fake, o => o.SidecarToken = "wrong");
        var auth = await Assert.ThrowsAsync<AiRequestException>(() => wrong.EmbedImageAsync(Picture(), default));
        Assert.Equal(401, auth.StatusCode);
        Assert.Contains("credentials", auth.Message);
        Assert.Equal(1, fake.CallsTo("/v1/embed/image"));
        Assert.DoesNotContain("wrong", auth.Message);

        using var provider = Provider(fake, o => o.SidecarToken = "right");
        await using var tiny = new MemoryStream(new byte[10]);
        var refused = await Assert.ThrowsAsync<AiRequestException>(() => provider.TranscribeAsync(tiny, new TranscribeOptions(), default));
        Assert.Equal(400, refused.StatusCode);
        Assert.Contains("audio too small", refused.Message);
        Assert.Equal(1, fake.CallsTo("/v1/transcribe"));
    }

    [Fact]
    public async Task An_unconfigured_or_unreachable_sidecar_reports_it_in_health_without_throwing()
    {
        using var none = new SelfHostedProvider(new AiOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfHostedProvider>.Instance);
        Assert.Equal("unconfigured", (await none.CheckHealthAsync(default)).Status);
        await Assert.ThrowsAsync<AiUnavailableException>(() => none.EmbedTextAsync("x", new TextEmbedOptions(), default));

        using var dead = new SelfHostedProvider(new AiOptions { SidecarUrl = "http://127.0.0.1:1", HealthTimeout = TimeSpan.FromSeconds(2) }, Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfHostedProvider>.Instance);
        Assert.Equal("down", (await dead.CheckHealthAsync(default)).Status);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class OpenAiCompatibleProviderTests
{
    private static OpenAiCompatibleProvider Provider(FakeOpenAi fake, Action<AiOptions>? tune = null, CapturingLoggerProvider? logs = null)
    {
        var o = new AiOptions
        {
            BaseUrl = fake.Url + "/v1", ApiKey = fake.ExpectedKey, RetryBaseDelay = TimeSpan.FromMilliseconds(1), TextDim = fake.EmbeddingDim,
            VisionModel = "vision-x", ChatModel = "chat-x", EmbeddingModel = "embed-x", TranscriptionModel = "whisper-x",
        };
        tune?.Invoke(o);
        using var factory = LoggerFactory.Create(b => { if (logs is not null) b.AddProvider(logs); });
        return new OpenAiCompatibleProvider(o, factory.CreateLogger<OpenAiCompatibleProvider>());
    }

    private static MemoryStream Picture() => new(TestImages.Solid(220, 30, 30));

    [Fact]
    public async Task Tags_and_ocr_are_a_vision_chat_call_with_the_picture_inline_and_a_strict_json_answer()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var provider = Provider(fake);

        var tags = await provider.TagImageAsync(Picture(), new TagOptions(TopK: 5, Labels: ["car"]), default);
        Assert.Equal("vision-x", tags.Model);
        Assert.Contains(tags.Tags, t => t.Label == "Outdoor");
        var call = fake.Calls.Single(c => c.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.Equal($"Bearer {fake.ExpectedKey}", call.Authorization);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("vision-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
        var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("at most 5 labels", system);
        Assert.Contains("[\"car\"]", system);
        var image = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/jpeg;base64,", image);

        var ocr = await provider.OcrAsync(Picture(), new OcrOptions("hi"), default);
        Assert.Equal(("Acme roadster showroom opening hours", "en"), (ocr.Text, ocr.Language));
        Assert.Contains("'hi'", JsonDocument.Parse(fake.Calls.Last().Body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_model_that_wraps_its_json_in_prose_and_a_code_fence_is_still_understood()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        fake.WrapJsonInFence = true;
        using var provider = Provider(fake);
        var tags = await provider.TagImageAsync(Picture(), new TagOptions(), default);
        Assert.NotEmpty(tags.Tags);
    }

    [Fact]
    public async Task Text_embeddings_ask_for_the_configured_size_and_come_back_unit_length_in_order()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var provider = Provider(fake, o => o.EmbeddingModel = "text-embedding-3-small");
        var r = await provider.EmbedTextsAsync(["red suv", "mountain snow"], new TextEmbedOptions(), default);
        Assert.Equal(2, r.Vectors.Count);
        Assert.All(r.Vectors, v => Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 4));   // the service answered with length 3: the provider normalised
        Assert.True(VectorMath.Cosine(r.Vectors[0], FakeSpace.Vector("red suv", 384)) > 0.999);
        var body = JsonDocument.Parse(fake.Calls.Last().Body).RootElement;
        Assert.Equal(384, body.GetProperty("dimensions").GetInt32());   // text-embedding-3 models can be asked for a size
        Assert.Equal(2, body.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task A_model_without_the_dimensions_option_is_not_sent_it_and_a_size_mismatch_is_an_actionable_error()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var ok = Provider(fake, o => o.EmbeddingModel = "bge-small");
        await ok.EmbedTextAsync("x", new TextEmbedOptions(), default);
        Assert.False(JsonDocument.Parse(fake.Calls.Last().Body).RootElement.TryGetProperty("dimensions", out _));

        using var wrong = Provider(fake, o => o.TextDim = 1536);
        var ex = await Assert.ThrowsAsync<AiRequestException>(() => wrong.EmbedTextAsync("x", new TextEmbedOptions(), default));
        Assert.Contains("DAM_AI_TEXT_DIM", ex.Message);
        Assert.Contains("384", ex.Message);
    }

    [Fact]
    public async Task Transcription_asks_for_timestamps_and_maps_seconds_to_milliseconds()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var provider = Provider(fake);
        await using var audio = new MemoryStream(TestImages.Wav(1));
        var r = await provider.TranscribeAsync(audio, new TranscribeOptions("en", 60, "speech.mp3"), default);
        Assert.Equal(new TranscriptSegment(0, 2500, "Welcome to the Acme showroom"), r.Segments[0]);
        Assert.Equal(new TranscriptSegment(2500, 6000, "Our new roadster has a panoramic roof"), r.Segments[1]);
        Assert.Equal(("en", 9000L, "whisper-x"), (r.Language, r.DurationMs, r.Model));   // "english" became "en"
        var body = fake.Calls.Last().Body;
        Assert.Contains("response_format=verbose_json", body);
        Assert.Contains("timestamp_granularities[]=segment", body);
        Assert.Contains("model=whisper-x", body);
        Assert.Contains("files=file:speech.mp3:", body);
    }

    [Fact]
    public async Task Image_embeddings_are_declared_unsupported_and_translate_and_summarise_work_through_chat()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var provider = Provider(fake);
        Assert.False(provider.Capabilities.ImageEmbeddings);
        await Assert.ThrowsAsync<AiNotSupportedException>(() => provider.EmbedImageAsync(Picture(), default));
        await Assert.ThrowsAsync<AiNotSupportedException>(() => provider.EmbedTextAsync("x", new TextEmbedOptions(TextEmbeddingKind.Clip), default));

        var t = await provider.TranslateAsync("Hello", "English", "Hindi", new Glossary(new Dictionary<string, string> { ["Acme Roadster"] = "" }), default);
        Assert.Equal("A translated or summarised text.", t.Text);
        Assert.Contains("Acme Roadster", JsonDocument.Parse(fake.Calls.Last().Body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var s = await provider.SummariseAsync("A long text", new SummaryOptions(50, "English"), default);
        Assert.Equal("chat-x", s.Model);
        Assert.Contains("50 words", JsonDocument.Parse(fake.Calls.Last().Body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var lang = await provider.DetectLanguageAsync("some words of english", default);
        Assert.Equal("en", lang.Language);
    }

    [Fact]
    public async Task Azure_style_services_get_the_key_in_the_api_key_header_a_model_in_the_path_and_an_api_version()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var provider = Provider(fake, o => { o.BaseUrl = fake.Url + "/openai/deployments/{model}"; o.AuthStyle = "api-key"; o.ApiVersion = "2024-06-01"; });
        await provider.EmbedTextAsync("x", new TextEmbedOptions(), default);
        var call = fake.Calls.Last();
        Assert.Equal("/openai/deployments/embed-x/embeddings?api-version=2024-06-01", call.Path);
        Assert.Equal($"api-key {fake.ExpectedKey}", call.Authorization);
    }

    [Fact]
    public async Task The_api_key_never_appears_in_logs_or_error_messages_even_when_the_service_echoes_it()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        fake.EchoKeyInErrors = true;
        var logs = new CapturingLoggerProvider();
        const string leaked = "sk-this-key-is-wrong-0123456789abcdef";
        using var provider = Provider(fake, o => o.ApiKey = leaked, logs);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => provider.EmbedTextAsync("x", new TextEmbedOptions(), default));
        Assert.Equal(401, ex.StatusCode);
        Assert.DoesNotContain(leaked, ex.Message);
        Assert.Contains("***", ex.Message);                 // it was there, and was scrubbed

        fake.ExpectedKey = leaked;
        fake.Down = true;
        var outage = await Assert.ThrowsAsync<AiUnavailableException>(() => provider.EmbedTextAsync("x", new TextEmbedOptions(), default));
        Assert.DoesNotContain(leaked, outage.Message);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(leaked, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("sk-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_is_a_one_word_embedding_that_proves_the_address_the_key_and_the_model()
    {
        await using var fake = await FakeOpenAi.StartAsync();
        using var good = Provider(fake);
        var h = await good.CheckHealthAsync(default);
        Assert.Equal("ok", h.Status);
        Assert.Equal("chat-x", h.Models!["chat"]);

        using var badKey = Provider(fake, o => o.ApiKey = "nope-nope-nope");
        var down = await badKey.CheckHealthAsync(default);
        Assert.Equal("down", down.Status);
        Assert.Contains("check the API key", down.Detail);
        Assert.DoesNotContain("nope-nope-nope", down.Detail);
    }
}
