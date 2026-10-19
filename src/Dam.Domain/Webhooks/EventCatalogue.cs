using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dam.Domain.Webhooks;

/// <param name="Category">asset, collection, workflow, comment, rights or webhook.</param>
/// <param name="Derived">Not raised by the domain itself: produced from another event for subscribers (see <see cref="EventCatalogue.Expand"/>).</param>
public sealed record EventTypeInfo(string Name, string Category, string Description, bool Derived = false);

/// <summary>An event as webhooks and chat channels see it.</summary>
/// <param name="Data">The domain event's own fields, with camelCase names (<c>EventId</c> and <c>OccurredAt</c> are hoisted to the envelope).</param>
public sealed record PublicEvent(Guid Id, string Type, DateTimeOffset OccurredAt, JsonElement Data);

/// <summary>
/// The events outside parties can subscribe to (spec A4.3), with the domain events that produce them. Webhooks and chat channels only
/// ever see these; internal events (ingest steps, role and access-rule changes...) are never delivered. The spec's
/// <c>asset.published</c>, <c>asset.expired</c>, <c>asset.archived</c> and <c>asset.deleted</c> are derived from <c>asset.status.changed</c>.
/// </summary>
public static class EventCatalogue
{
    public const string Ping = "webhook.ping";

    public static readonly IReadOnlyList<EventTypeInfo> All =
    [
        new("asset.created", "asset", "An asset was created (a draft shell, before any file)."),
        new("asset.metadata.updated", "asset", "An asset's title, metadata, tags or placement changed."),
        new("asset.status.changed", "asset", "An asset moved to another lifecycle status (draft, in_review, approved, published, expired, archived, deleted)."),
        new("asset.published", "asset", "An asset was published. Derived from asset.status.changed.", true),
        new("asset.expired", "asset", "A published asset expired. Derived from asset.status.changed.", true),
        new("asset.archived", "asset", "An asset was archived. Derived from asset.status.changed.", true),
        new("asset.deleted", "asset", "An asset was deleted (purged). Derived from asset.status.changed.", true),
        new("asset.version.added", "asset", "A new version (file) became the asset's current version."),
        new("asset.legal_hold.changed", "asset", "Legal hold was placed on or released from an asset."),
        new("rendition.ready", "asset", "A rendition of a version (thumbnail, preview, transcode...) is ready to deliver."),
        new("collection.updated", "collection", "A collection was renamed, re-queried or its visibility changed."),
        new("collection.assets_changed", "collection", "Assets were added to or removed from a static collection."),
        new("workflow.task.created", "workflow", "An approval task was opened for an asset."),
        new("workflow.task.completed", "workflow", "An approval task was approved or rejected."),
        new("workflow.sla.breached", "workflow", "An approval task passed its deadline and was escalated."),
        new("comment.added", "comment", "A comment or reply was added to an asset (the payload names the people mentioned; it never carries the text)."),
        new("comment.resolved", "comment", "A comment thread was marked resolved."),
        new("comment.reopened", "comment", "A resolved comment thread was reopened."),
        new("rights.expiring", "rights", "The licence assigned to an asset is within 30, 7 or 1 days of ending (the payload names the asset, the licence, the end date, the days remaining and the threshold reached)."),
        new("rights.expired", "rights", "The licence assigned to an asset has ended (the payload names the asset, the licence and its end date)."),
        new(Ping, "webhook", "A test event sent by the Ping button; never raised by the system itself."),
    ];

    private static readonly HashSet<string> Names = All.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>The domain events the consumers must listen to (everything in the catalogue that is neither derived nor synthetic).</summary>
    public static readonly IReadOnlyList<string> SourceEvents = All.Where(e => !e.Derived && e.Name != Ping).Select(e => e.Name).ToList();

    public static bool IsKnown(string name) => Names.Contains(name);

    private static readonly string[] DerivedStatuses = ["published", "expired", "archived", "deleted"];

    /// <summary>
    /// The public events one domain event stands for: itself, plus <c>asset.&lt;status&gt;</c> for a status change to published, expired,
    /// archived or deleted. Empty for events that are not in the catalogue. Derived ids are derived deterministically from the original
    /// so a redelivery produces the same ids (and so de-duplicates).
    /// </summary>
    public static IReadOnlyList<PublicEvent> Expand(string name, string payloadJson, Guid fallbackId, DateTimeOffset fallbackTime)
    {
        if (!IsKnown(name) || name == Ping) return [];
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("EventId", out var e) && e.TryGetGuid(out var g) ? g : fallbackId;
        var at = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("OccurredAt", out var o) && o.TryGetDateTimeOffset(out var t) ? t : fallbackTime;
        var data = JsonCase.ToCamel(root, skip: ["EventId", "OccurredAt"]);

        var result = new List<PublicEvent> { new(id, name, at, data) };
        if (name == "asset.status.changed" && data.TryGetProperty("to", out var to) && to.GetString() is { } status && DerivedStatuses.Contains(status))
            result.Add(new PublicEvent(DerivedId(id, "asset." + status), "asset." + status, at, data));
        return result;
    }

    internal static Guid DerivedId(Guid source, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString("N") + ":" + name));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);   // version 5-style, so the id is recognisably derived
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

/// <summary>
/// Event subscriptions: an exact name (<c>asset.published</c>), a prefix wildcard (<c>asset.*</c> matches <c>asset.status.changed</c>
/// and every other name beginning <c>asset.</c>) or <c>*</c> for everything. Names are lower-case dotted words.
/// </summary>
public static partial class EventPattern
{
    [GeneratedRegex("^(\\*|[a-z][a-z0-9_]*(\\.[a-z0-9_]+)*(\\.\\*)?)$")]
    private static partial Regex Valid();

    public static bool IsValid(string pattern) => pattern.Length <= 100 && Valid().IsMatch(pattern);

    public static bool Matches(string pattern, string eventName)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith(".*", StringComparison.Ordinal)) return eventName.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return string.Equals(pattern, eventName, StringComparison.Ordinal);
    }

    public static bool MatchesAny(IEnumerable<string> patterns, string eventName) => patterns.Any(p => Matches(p, eventName));

    /// <summary>Trims, lower-cases, drops empties and duplicates, keeps the order.</summary>
    public static List<string> Normalize(IEnumerable<string>? patterns) =>
        (patterns ?? []).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList();
}

/// <summary>Event payloads are serialised with .NET names (<c>AssetId</c>); outside parties get conventional camelCase.</summary>
public static class JsonCase
{
    public static JsonElement ToCamel(JsonElement source, IReadOnlyCollection<string>? skip = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
            Write(w, source, skip, top: true);
        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter w, JsonElement e, IReadOnlyCollection<string>? skip, bool top)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    if (top && skip?.Contains(p.Name) == true) continue;
                    w.WritePropertyName(p.Name.Length > 0 ? char.ToLowerInvariant(p.Name[0]) + p.Name[1..] : p.Name);
                    Write(w, p.Value, skip, top: false);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) Write(w, item, skip, top: false);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}

/// <summary>The JSON a webhook receiver gets: <c>{ id, type, tenantId, occurredAt, data }</c>.</summary>
public static class WebhookEnvelope
{
    public static string Build(PublicEvent e, Guid tenantId) => Build(e.Id, e.Type, tenantId, e.OccurredAt, e.Data);

    public static string Build(Guid id, string type, Guid tenantId, DateTimeOffset occurredAt, JsonElement data) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id,
            ["type"] = type,
            ["tenantId"] = tenantId,
            ["occurredAt"] = occurredAt.ToUniversalTime(),
            ["data"] = data,
        });
}
