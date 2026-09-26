using System.Text;
using System.Text.RegularExpressions;

namespace Dam.Domain.Content;

/// <summary>
/// Hierarchy paths are dotted labels (ltree syntax): "brand.roadster.stills". Labels are lowercase letters, digits and
/// underscores. Stored as text with a format check; converting the column to ltree later is a one-line migration.
/// </summary>
public static partial class Paths
{
    public const int MaxLabelLength = 64;
    public const int MaxDepth = 12;

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex LabelRegex();

    public static bool IsValidLabel(string? label) => label is not null && LabelRegex().IsMatch(label);

    public static bool IsValidPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Split('.').Length <= MaxDepth && path.Split('.').All(IsValidLabel);

    public static int Depth(string path) => path.Count(c => c == '.') + 1;

    public static string Join(string? parentPath, string label) => parentPath is null ? label : $"{parentPath}.{label}";

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> itself or lies beneath it.</summary>
    public static bool IsSelfOrDescendant(string ancestor, string path) =>
        path == ancestor || path.StartsWith(ancestor + ".", StringComparison.Ordinal);

    /// <summary>Turns a display name into a valid label: "Thar Hero Shots!" -> "thar_hero_shots".</summary>
    public static string Slugify(string name, string fallback = "item")
    {
        var sb = new StringBuilder();
        var lastUnderscore = true; // swallow leading separators
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (ch < 128 && char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); lastUnderscore = false; }
            else if (ch < 128 && !lastUnderscore) { sb.Append('_'); lastUnderscore = true; } // accents fall away, other ASCII separates
        }
        var slug = sb.ToString().Trim('_');
        if (slug.Length == 0) slug = fallback;
        return slug.Length > MaxLabelLength ? slug[..MaxLabelLength].TrimEnd('_') : slug;
    }

    /// <summary>First free label among siblings: "reports", "reports_2", "reports_3"…</summary>
    public static string Unique(string label, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(label)) return label;
        for (var i = 2; ; i++)
        {
            var suffix = "_" + i;
            var candidate = (label.Length + suffix.Length > MaxLabelLength ? label[..(MaxLabelLength - suffix.Length)] : label) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
