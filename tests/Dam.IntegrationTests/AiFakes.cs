using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetVips;

namespace Dam.IntegrationTests;

/// <summary>
/// A tiny made-up "world" of concepts, so embeddings in the tests are deterministic and meaningful: every word that names a concept adds weight to that
/// concept's axis, and a text and a picture land close together exactly when they share concepts. Words that name nothing share one "nothing" axis.
/// </summary>
public static class FakeSpace
{
    public static readonly string[][] Concepts =
    [
        ["red", "crimson", "scarlet"],
        ["suv", "car", "vehicle", "automobile", "roadster"],
        ["beach", "sand", "coast", "shore", "seaside"],
        ["mountain", "snow", "alpine", "peak"],
        ["office", "desk", "meeting", "boardroom"],
        ["dog", "puppy", "hound"],
        ["kitchen", "cooking", "chef"],
        ["city", "street", "skyline"],
    ];

    public static int Axis(string concept) => Array.FindIndex(Concepts, c => c.Contains(concept));

    public static float[] Vector(string text, int dim)
    {
        var v = new float[dim];
        var hit = false;
        foreach (var word in text.ToLowerInvariant().Split([' ', ',', '.', '-', '_', '\n', ':', ';', '!', '?', '\'', '"'], StringSplitOptions.RemoveEmptyEntries))
        {
            var axis = Array.FindIndex(Concepts, c => c.Contains(word));
            if (axis < 0) continue;
            v[axis] += 1;
            hit = true;
        }
        if (!hit) v[dim - 1] = 1;   // "nothing in particular"
        return Normalize(v);
    }

    public static float[] Concept(int dim, params string[] concepts)
    {
        var v = new float[dim];
        foreach (var c in concepts) v[Axis(c)] += 1;
        return Normalize(v);
    }

    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }
}

public sealed record RecordedCall(string Method, string Path, string? Authorization, string? ContentType, string Body);

/// <summary>
/// An in-process stand-in for the AI sidecar, following its HTTP contract, with deterministic answers: a picture's "meaning" depends on its dominant colour
/// (red is a red SUV on a beach, blue a mountain, green an office), text vectors come from <see cref="FakeSpace"/>. Failures can be switched on.
/// </summary>
public sealed class FakeSidecar : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<RecordedCall> Calls { get; } = new();
    /// <summary>What kind of file each upload was (webp, jpeg, png, mp3...), by path: proves the worker sends renditions, never the original.</summary>
    public ConcurrentQueue<(string Path, string Format, int Length)> Uploads { get; } = new();

    public string Url { get; }
    public string? RequiredToken { get; set; }
    private int _fail503;
    /// <summary>Answer this many requests with 503 + Retry-After 0, then recover.</summary>
    public int Fail503
    {
        get => Volatile.Read(ref _fail503);
        set => Interlocked.Exchange(ref _fail503, value);
    }

    private bool TakeFailure()
    {
        while (true)
        {
            var current = Volatile.Read(ref _fail503);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _fail503, current - 1, current) == current) return true;
        }
    }
    /// <summary>Answer every request with 503.</summary>
    public bool Down { get; set; }
    public int TextDim { get; set; } = 384;
    public int ImageDim { get; set; } = 512;
    public string OcrText { get; set; } = "Acme roadster showroom opening hours";
    public string Language { get; set; } = "en";
    public List<(long Start, long End, string Text)> Segments { get; set; } =
    [
        (0, 2500, "Welcome to the Acme showroom"), (2500, 6000, "Our new roadster has a panoramic roof"), (6000, 9000, "Come and drive one this weekend"),
    ];
    public string Model { get; set; } = "fake-sidecar";

    public int CallsTo(string path) => Calls.Count(c => c.Path == path);

    private FakeSidecar(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<FakeSidecar> StartAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        FakeSidecar? self = null;

        app.Use(async (ctx, next) =>
        {
            var s = self!;
            string body = "";
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync();
                body = string.Join("&", form.Select(f => $"{f.Key}={f.Value}")) + "&files=" + string.Join(",", form.Files.Select(f => $"{f.Name}:{f.FileName}:{f.Length}"));
            }
            else
            {
                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
            }
            s.Calls.Enqueue(new RecordedCall(ctx.Request.Method, ctx.Request.Path, ctx.Request.Headers.Authorization, ctx.Request.ContentType, body));
            if (ctx.Request.Path != "/healthz")
            {
                if (s.RequiredToken is { } token && ctx.Request.Headers.Authorization != $"Bearer {token}")
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized", message = "bad token" });
                    return;
                }
                if (s.Down || s.TakeFailure())
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Headers.RetryAfter = "0";
                    await ctx.Response.WriteAsJsonAsync(new { error = "busy", message = "try again" });
                    return;
                }
            }
            await next();
        });

        app.MapGet("/healthz", (HttpContext _) => Results.Json(new
        {
            status = "ok",
            version = "0.0-test",
            models = new { clip = new { name = "clip-fake", dim = 512, loaded = true }, text = new { name = "e5-fake", dim = 384, loaded = true }, whisper = new { name = "whisper-fake", loaded = true }, ocr = new { name = "ocr-fake", loaded = false } },
        }));
        app.MapPost("/v1/embed/image", async (HttpContext ctx) =>
        {
            var bytes = await self!.FileBytes(ctx);
            if (bytes.Length == 0) return Results.Json(new { error = "bad_request", message = "no file" }, statusCode: 400);
            return Results.Json(new { model = self!.Model + "-clip", dim = self.ImageDim, embedding = ImageVector(bytes, self.ImageDim) });
        });
        app.MapPost("/v1/embed/text", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var kind = doc.RootElement.GetProperty("kind").GetString();
            var dim = kind == "clip" ? self!.ImageDim : self!.TextDim;
            var vectors = doc.RootElement.GetProperty("texts").EnumerateArray().Select(t => FakeSpace.Vector(t.GetString()!, dim)).ToList();
            return Results.Json(new { model = self.Model + (kind == "clip" ? "-clip-text" : "-e5"), dim, embeddings = vectors });
        });
        app.MapPost("/v1/tag", async (HttpContext ctx) =>
        {
            var bytes = await self!.FileBytes(ctx);
            // Like the real sidecar, the cut-off is the caller's threshold when given, else its own default.
            var threshold = ctx.Request.Form.TryGetValue("threshold", out var t) && float.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var given) ? given : 0.1f;
            var tags = TagsFor(bytes).Cast<dynamic>().Where(x => (double)x.score >= threshold).ToArray();
            return Results.Json(new { model = self!.Model + "-tagger", tags });
        });
        app.MapPost("/v1/ocr", async (HttpContext ctx) =>
        {
            await self!.FileBytes(ctx);
            var text = self!.OcrText;
            return Results.Json(new { model = self.Model + "-ocr", language = self.Language, text, lines = text.Split(' ').Chunk(3).Select((w, i) => new { text = string.Join(' ', w), score = 0.9, box = new[] { 0, i * 10, 100, i * 10 + 8 } }) });
        });
        app.MapPost("/v1/transcribe", async (HttpContext ctx) =>
        {
            var bytes = await self!.FileBytes(ctx);
            if (bytes.Length < 100) return Results.Json(new { error = "bad_request", message = "audio too small" }, statusCode: 400);
            var s = self!;
            return Results.Json(new
            {
                model = s.Model + "-whisper",
                language = s.Language,
                duration = s.Segments.Count == 0 ? 0 : s.Segments[^1].End / 1000.0,
                segments = s.Segments.Select(x => new { startMs = x.Start, endMs = x.End, text = x.Text }),
            });
        });
        app.MapPost("/v1/detect-language", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            return Results.Json(text.Contains("hola", StringComparison.OrdinalIgnoreCase) ? new { language = "es", confidence = 0.97 } : new { language = self!.Language, confidence = 0.95 });
        });

        await app.StartAsync();
        var url = app.Urls.First();
        self = new FakeSidecar(app, url);
        return self;
    }

    private async Task<byte[]> FileBytes(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file is null) return [];
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        Uploads.Enqueue((ctx.Request.Path, FormatOf(bytes), bytes.Length));
        return bytes;
    }

    public static string FormatOf(byte[] b) =>
        b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' ? "webp"
        : b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 ? "jpeg"
        : b.Length > 4 && b[0] == 0x89 && b[1] == 'P' ? "png"
        : b.Length > 4 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' ? "wav"
        : b.Length > 3 && (b[0] == 'I' && b[1] == 'D' && b[2] == '3' || b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) ? "mp3"
        : "other";

    /// <summary>The colour of a picture decides what it "shows": red is a red SUV on a beach, blue a mountain, green an office, anything else a dog.</summary>
    public static string[] ConceptsOf(byte[] image)
    {
        try
        {
            using var img = Image.NewFromBuffer(image);
            using var rgb = img.Bands >= 3 ? img : img.Bandjoin(img, img);
            var means = Enumerable.Range(0, 3).Select(b => { using var band = rgb.ExtractBand(b); return band.Avg(); }).ToArray();
            if (means[0] > means[1] + 30 && means[0] > means[2] + 30) return ["red", "suv", "beach"];
            if (means[2] > means[0] + 30 && means[2] > means[1] + 30) return ["mountain"];
            if (means[1] > means[0] + 30 && means[1] > means[2] + 30) return ["office"];
        }
        catch (VipsException) { /* not a picture: fall through */ }
        return ["dog"];
    }

    public static float[] ImageVector(byte[] image, int dim) => FakeSpace.Concept(dim, ConceptsOf(image));

    /// <summary>Tags a real tagger might give: generic words that deliberately share nothing with the concept words.</summary>
    public static object[] TagsFor(byte[] image) => ConceptsOf(image) switch
    {
    ["red", ..] => [new { label = "Outdoor", score = 0.91 }, new { label = "travel", score = 0.62 }, new { label = "hero", score = 0.55 }, new { label = "blurry", score = 0.05 }],
    ["mountain"] => [new { label = "landscape", score = 0.88 }, new { label = "outdoor", score = 0.7 }],
    ["office"] => [new { label = "interior", score = 0.8 }],
        _ => [new { label = "animal", score = 0.9 }],
    };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>An in-process stand-in for an OpenAI-compatible service: chat completions (tags, OCR, language), embeddings and transcriptions.</summary>
public sealed class FakeOpenAi : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<RecordedCall> Calls { get; } = new();
    public string Url { get; }
    public string ExpectedKey { get; set; } = "sk-test-secret-key-0123456789";
    public bool Down { get; set; }
    public int EmbeddingDim { get; set; } = 384;
    public bool WrapJsonInFence { get; set; }
    /// <summary>Echo the key back in error messages, as some services do (the provider must scrub it).</summary>
    public bool EchoKeyInErrors { get; set; }
    public string OcrText { get; set; } = "Acme roadster showroom opening hours";

    /// <summary>One translation call as the service saw it: the instruction, the text it was asked to translate, and the languages.</summary>
    public sealed record TranslateRequest(string System, string Text, string From, string To);

    /// <summary>Every translation the service was asked for, in order: proves what left the building (glossary terms must be tokens, not words).</summary>
    public ConcurrentQueue<TranslateRequest> TranslateRequests { get; } = new();

    /// <summary>What the "model" answers when asked to translate: the text, the source and the target language. By default a faithful, literal translator: the
    /// target code and the text in capitals, with every placeholder left alone. A test replaces it to drop, change, repeat or invent placeholders.</summary>
    public Func<string, string, string, string>? TranslateWith { get; set; }

    /// <summary>Answer translation calls with a 400, as a service that refuses the input would.</summary>
    public bool TranslateRefused { get; set; }

    /// <summary>Answer translation calls as if the model ran out of room (finish_reason "length").</summary>
    public bool TranslateCutOff { get; set; }

    /// <summary>What the "model" answers when asked to summarise.</summary>
    public Func<string, string>? SummariseWith { get; set; }

    public int Summaries => Calls.Count(c => c.Path.EndsWith("chat/completions", StringComparison.Ordinal) && c.Body.Contains("Summarise the user", StringComparison.Ordinal));

    public int CallsTo(string path) => Calls.Count(c => c.Path.EndsWith(path, StringComparison.Ordinal));

    private FakeOpenAi(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<FakeOpenAi> StartAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        FakeOpenAi? self = null;

        app.Use(async (ctx, next) =>
        {
            var s = self!;
            var body = "";
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync();
                body = string.Join("&", form.Select(f => $"{f.Key}={f.Value}")) + "&files=" + string.Join(",", form.Files.Select(f => $"{f.Name}:{f.FileName}:{f.Length}"));
            }
            else
            {
                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
            }
            ctx.Items["body"] = body;
            var auth = ctx.Request.Headers.Authorization.ToString();
            var apiKey = ctx.Request.Headers["api-key"].ToString();
            s.Calls.Enqueue(new RecordedCall(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, auth.Length > 0 ? auth : apiKey.Length > 0 ? "api-key " + apiKey : null, ctx.Request.ContentType, body));
            var presented = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : apiKey;
            if (presented != s.ExpectedKey)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = new { message = s.EchoKeyInErrors ? $"Incorrect API key provided: {presented}." : "Incorrect API key provided.", type = "invalid_request_error" } });
                return;
            }
            if (s.Down) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { error = new { message = "overloaded" } }); return; }
            await next();
        });

        app.MapPost("/{**path}", (HttpContext ctx, string path) =>
        {
            var s = self!;
            if (path.EndsWith("chat/completions", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse((string)ctx.Items["body"]!);
                var messages = doc.RootElement.GetProperty("messages");
                var system = messages[0].GetProperty("content").GetString() ?? "";
                var user = messages[1].GetProperty("content");
                string content;
                var finish = "stop";
                if (system.Contains("Translate the user's text", StringComparison.Ordinal))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(system, @"from (\S+) to (\S+)\.");
                    var (from, to) = (match.Groups[1].Value, match.Groups[2].Value);
                    var text = user.GetString() ?? "";
                    s.TranslateRequests.Enqueue(new TranslateRequest(system, text, from, to));
                    if (s.TranslateRefused) return Results.Json(new { error = new { message = "The input was refused." } }, statusCode: 400);
                    content = (s.TranslateWith ?? ((t, _, target) => $"{target}: {t.ToUpperInvariant()}"))(text, from, to);
                    if (s.TranslateCutOff) finish = "length";
                }
                else if (system.Contains("Summarise the user's text", StringComparison.Ordinal))
                {
                    var text = user.GetString() ?? "";
                    content = (s.SummariseWith ?? (t => "Overview: " + string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(12)) + " summarymarker"))(text);
                }
                else if (system.Contains("label pictures", StringComparison.Ordinal))
                {
                    var data = user.EnumerateArray().First(p => p.GetProperty("type").GetString() == "image_url").GetProperty("image_url").GetProperty("url").GetString()!;
                    var bytes = Convert.FromBase64String(data[(data.IndexOf("base64,", StringComparison.Ordinal) + 7)..]);
                    content = JsonSerializer.Serialize(new { tags = FakeSidecar.TagsFor(bytes) });
                }
                else if (system.Contains("read text from pictures", StringComparison.Ordinal)) content = JsonSerializer.Serialize(new { language = "en", text = s.OcrText });
                else if (system.Contains("language of the text", StringComparison.Ordinal)) content = JsonSerializer.Serialize(new { language = "en", confidence = 0.9 });
                else content = "A translated or summarised text.";
                if (s.WrapJsonInFence && content.StartsWith('{')) content = "Here you go:\n```json\n" + content + "\n```";
                return Results.Json(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } } });
            }
            if (path.EndsWith("embeddings", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse((string)ctx.Items["body"]!);
                var input = doc.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()!).ToList();
                // Not normalised on purpose: the provider must do it.
                return Results.Json(new { model = "fake-embedding", data = input.Select((t, i) => new { index = i, embedding = FakeSpace.Vector(t, s.EmbeddingDim).Select(x => x * 3).ToArray() }) });
            }
            if (path.EndsWith("audio/transcriptions", StringComparison.Ordinal))
                return Results.Json(new
                {
                    language = "english",
                    duration = 9.0,
                    text = "Welcome to the Acme showroom Our new roadster",
                    segments = new[] { new { id = 0, start = 0.0, end = 2.5, text = " Welcome to the Acme showroom" }, new { id = 1, start = 2.5, end = 6.0, text = " Our new roadster has a panoramic roof" } },
                });
            return Results.NotFound();
        });

        await app.StartAsync();
        self = new FakeOpenAi(app, app.Urls.First());
        return self;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>Captures what a component logs, so a test can prove a secret never appears in it.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);
    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }
}
