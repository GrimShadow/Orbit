using System.Text.Json;
using Dam.Domain.Ai;

namespace Dam.Application.Ai;

/// <summary>The language settings a tenant keeps in its settings document: <c>languages</c> (the codes multilingual metadata may use; empty or absent means any)
/// and <c>defaultLanguage</c> (the language to translate from when none is named).</summary>
public sealed record TenantLanguages(IReadOnlySet<string>? Enabled, string? Default)
{
    public static readonly TenantLanguages Unrestricted = new(null, null);

    public bool Allows(string language) => Enabled is null || Enabled.Contains(language);

    public static TenantLanguages Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return Unrestricted;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unrestricted;
            IReadOnlySet<string>? enabled = null;
            if (root.TryGetProperty("languages", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                var set = l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant()).ToHashSet();
                if (set.Count > 0) enabled = set;
            }
            var def = root.TryGetProperty("defaultLanguage", out var d) && d.ValueKind == JsonValueKind.String ? TranslationRules.NormalizeLanguage(d.GetString()) : null;
            return new TenantLanguages(enabled, def);
        }
        catch (JsonException) { return Unrestricted; }   // malformed settings: no restriction, like the metadata validator
    }
}
