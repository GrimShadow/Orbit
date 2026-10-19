using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dam.Domain.Comments;

public static class AnchorTypes
{
    public const string Region = "region";
    public const string Point = "point";
    public const string Timecode = "timecode";
    public static readonly IReadOnlyList<string> All = [Region, Point, Timecode];
}

/// <summary>
/// Where a comment points (ADR-0032). Image and page positions are fractions of the rendered width and height, so they mean the same thing
/// at any zoom or on any rendition; time positions are milliseconds. Pure and dependency-free: the same rules run on the way in (422 with
/// the offending field named) and in the unit tests.
/// </summary>
public sealed record CommentAnchor(
    string Type, double? X = null, double? Y = null, double? W = null, double? H = null, int? Page = null, long? StartMs = null, long? EndMs = null)
{
    /// <summary>The longest position that makes sense: a day.</summary>
    public const long MaxMs = 24L * 60 * 60 * 1000;
    public const int MaxPage = 10_000;
    private const double Epsilon = 1e-9;

    public static bool IsTimeBased(string assetType) => assetType is "video" or "audio";

    /// <summary>Field errors keyed like the request (<c>anchor.x</c>, <c>anchor.type</c>); empty when the anchor is acceptable for this kind of asset.</summary>
    public IReadOnlyDictionary<string, string[]> Validate(string assetType)
    {
        var errors = new Dictionary<string, string[]>();
        void Fail(string field, string message) => errors["anchor." + field] = [message];

        if (!AnchorTypes.All.Contains(Type))
        {
            Fail("type", $"Use one of: {string.Join(", ", AnchorTypes.All)}.");
            return errors;
        }
        var timeBased = IsTimeBased(assetType);
        if (Type == AnchorTypes.Timecode && !timeBased) Fail("type", "A timecode only applies to video and audio. Mark a region or a point instead.");
        if (Type != AnchorTypes.Timecode && timeBased) Fail("type", "Video and audio are annotated with a timecode, not a region or a point.");

        if (Type == AnchorTypes.Timecode)
        {
            Unused("x", X); Unused("y", Y); Unused("w", W); Unused("h", H); Unused("page", Page);
            if (StartMs is not { } start) Fail("startMs", "Required.");
            else if (start is < 0 or > MaxMs) Fail("startMs", $"Must be between 0 and {MaxMs} milliseconds.");
            if (EndMs is { } end)
            {
                if (end > MaxMs) Fail("endMs", $"Must be at most {MaxMs} milliseconds.");
                else if (StartMs is { } s && end <= s) Fail("endMs", "Must be after startMs.");
            }
            return errors;
        }

        Unused("startMs", StartMs); Unused("endMs", EndMs);
        Fraction("x", X);
        Fraction("y", Y);
        if (Type == AnchorTypes.Region)
        {
            Size("w", W, X);
            Size("h", H, Y);
        }
        else
        {
            Unused("w", W); Unused("h", H);
        }
        if (Page is { } page && page is < 1 or > MaxPage) Fail("page", $"Must be between 1 and {MaxPage}.");
        return errors;

        void Unused<T>(string field, T? value) where T : struct
        {
            if (value is not null) Fail(field, $"Not used by a {Type} anchor.");
        }
        void Fraction(string field, double? value)
        {
            if (value is not { } v) Fail(field, "Required.");
            else if (!double.IsFinite(v) || v < 0 || v > 1) Fail(field, "Must be between 0 and 1.");
        }
        void Size(string field, double? size, double? origin)
        {
            if (size is not { } s) { Fail(field, "Required."); return; }
            if (!double.IsFinite(s) || s <= 0 || s > 1) Fail(field, "Must be greater than 0 and at most 1.");
            else if (origin is { } o && o is >= 0 and <= 1 && o + s > 1 + Epsilon)
                Fail(field, field == "w" ? "x + w must not exceed 1: the region leaves the image." : "y + h must not exceed 1: the region leaves the image.");
        }
    }

    /// <summary>The stored form: a JSON object with only the fields that apply, numbers written as given.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("type", Type);
            if (X is { } x) w.WriteNumber("x", x);
            if (Y is { } y) w.WriteNumber("y", y);
            if (W is { } width) w.WriteNumber("w", width);
            if (H is { } height) w.WriteNumber("h", height);
            if (Page is { } page) w.WriteNumber("page", page);
            if (StartMs is { } start) w.WriteNumber("startMs", start);
            if (EndMs is { } end) w.WriteNumber("endMs", end);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static CommentAnchor? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.GetString() is not { } t) return null;
        double? D(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        long? L(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        return new CommentAnchor(t, D("x"), D("y"), D("w"), D("h"), L("page") is { } p ? (int)p : null, L("startMs"), L("endMs"));
    }
}

/// <summary>
/// The text of a comment: plain text where a person is named by the token <c>@[Display Name](user:&lt;guid&gt;)</c>, so a renamed person
/// still resolves. Parsing, normalising what is kept, and the short excerpt notifications show.
/// </summary>
public static partial class CommentText
{
    public const int MaxLength = 4000;
    public const int MaxMentions = 20;
    private const int MaxLabel = 120;

    [GeneratedRegex(@"@\[(?<label>[^\[\]\r\n]{1,120})\]\(user:(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    /// <summary>The token for a person, with brackets and line breaks taken out of the name so it cannot break the syntax.</summary>
    public static string TokenFor(string displayName, Guid userId) => $"@[{Label(displayName)}](user:{userId:D})";

    /// <summary>The people named by tokens, each once, in the order they first appear.</summary>
    public static IReadOnlyList<Guid> MentionedIds(string body)
    {
        var ids = new List<Guid>();
        foreach (Match m in Token().Matches(body))
            if (Guid.TryParse(m.Groups["id"].Value, out var id) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>
    /// What gets stored. A token for someone in <paramref name="eligible"/> (id to current display name) is rewritten with that name, so the
    /// label can never claim to be somebody else; any other token (unknown, disabled, cannot see the asset) becomes plain <c>@Name</c>, which
    /// notifies nobody and reveals nothing about whether that person exists.
    /// </summary>
    public static string Normalize(string body, IReadOnlyDictionary<Guid, string> eligible) =>
        Token().Replace(body, m =>
            Guid.TryParse(m.Groups["id"].Value, out var id) && eligible.TryGetValue(id, out var name)
                ? TokenFor(name, id)
                : "@" + Label(m.Groups["label"].Value));

    /// <summary>The body for people and notifications: every token shown as <c>@Name</c>.</summary>
    public static string Plain(string body) => Token().Replace(body, m => "@" + m.Groups["label"].Value.Trim());

    /// <summary>One line, tokens as @Name, white space collapsed, cut at <paramref name="max"/> characters.</summary>
    public static string Excerpt(string body, int max = 140)
    {
        var flat = string.Join(' ', Plain(body).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    private static string Label(string name)
    {
        var cleaned = string.Join(' ', name.Replace('[', ' ').Replace(']', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0) cleaned = "user";
        return cleaned.Length <= MaxLabel ? cleaned : cleaned[..MaxLabel];
    }
}
