using System.Text;
using System.Text.RegularExpressions;

namespace Dam.Domain.Ai;

/// <summary>One protected occurrence: where it was, the token that stands for it, and what the token turns back into.</summary>
/// <param name="Index">1-based, in order of appearance.</param>
/// <param name="Original">The text that was replaced, exactly as it was.</param>
/// <param name="Replacement">What the token becomes after translation: the original for a locked term (or a mapped one without a rendering for the language), the rendering otherwise.</param>
public sealed record ProtectedSpan(int Index, string Token, string Original, string Replacement);

/// <summary>Text with its glossary terms swapped for opaque tokens, ready to send to a provider.</summary>
public sealed record ProtectedText(string Text, IReadOnlyList<ProtectedSpan> Spans, string Prefix)
{
    public bool HasTokens => Spans.Count > 0;
}

/// <param name="Problem">When not Ok: what went wrong with the tokens, in words a person can read.</param>
public sealed record RestoreResult(bool Ok, string Text, string? Problem);

/// <summary>
/// Keeps glossary terms out of the hands of the translator (ADR-0036). <see cref="Protect"/> swaps every occurrence of a term for a token such as
/// <c>⟦G1⟧</c>; the provider translates around the tokens; <see cref="Restore"/> swaps them back (a locked term to the original, a mapped term to the
/// rendering for the target language) and refuses the translation if any token was lost, repeated, altered or invented. The check, not the prompt, is
/// what makes "glossary terms are never translated" true for every provider.
/// <para>
/// Matching is ordinal and case-sensitive, longest term first, so a longer term claims its text before a shorter one inside it; occurrences that overlap
/// an earlier claim are left alone. A boundary check applies only where a term starts or ends with an ASCII letter or digit (it may not be part of a longer
/// word); Indic scripts have no reliable word boundary, so a term that starts or ends with one is matched wherever it appears.
/// </para>
/// </summary>
public static class GlossaryProtector
{
    public const char Open = '⟦', Close = '⟧';

    /// <summary>Every marker of the shape this run's tokens have, well formed or not a token that was sent.</summary>
    private static Regex AnyToken(string prefix) => new($"{Open}{Regex.Escape(prefix)}\\d+{Close}", RegexOptions.CultureInvariant);

    public static ProtectedText Protect(string text, IReadOnlyList<GlossaryEntry> entries, string targetLanguage)
    {
        var prefix = ChoosePrefix(text);
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return new ProtectedText(text, [], prefix);

        // Longest first (then by text, so equal lengths are decided the same way every time); each claims the stretches not already claimed.
        var ordered = entries.Where(e => e.Term.Length > 0 && e.Term.Length <= text.Length).OrderByDescending(e => e.Term.Length).ThenBy(e => e.Term, StringComparer.Ordinal).ToList();
        var claims = new List<(int Start, int End, GlossaryEntry Entry)>();
        foreach (var entry in ordered)
        {
            var from = 0;
            while (from <= text.Length - entry.Term.Length)
            {
                var at = text.IndexOf(entry.Term, from, StringComparison.Ordinal);
                if (at < 0) break;
                var end = at + entry.Term.Length;
                if (HasBoundary(text, entry.Term, at, end) && !claims.Any(c => at < c.End && end > c.Start)) claims.Add((at, end, entry));
                from = at + 1;
            }
        }

        var sb = new StringBuilder(text.Length + claims.Count * 6);
        var spans = new List<ProtectedSpan>();
        var cursor = 0;
        foreach (var (start, end, entry) in claims.OrderBy(c => c.Start))
        {
            sb.Append(text, cursor, start - cursor);
            var index = spans.Count + 1;
            var token = $"{Open}{prefix}{index}{Close}";
            sb.Append(token);
            spans.Add(new ProtectedSpan(index, token, text[start..end], entry.RenderIn(targetLanguage)));
            cursor = end;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return new ProtectedText(sb.ToString(), spans, prefix);
    }

    /// <summary>
    /// Swaps the tokens in <paramref name="translated"/> back for their text. Fails (and returns the text as received) when a token is missing, appears more than once,
    /// was changed in any way (spacing, case, brackets) or when a token that was never sent appears.
    /// </summary>
    public static RestoreResult Restore(string translated, ProtectedText sent)
    {
        if (!sent.HasTokens) return new RestoreResult(true, translated, null);

        var expected = sent.Spans.ToDictionary(s => s.Token, StringComparer.Ordinal);
        foreach (var span in sent.Spans)
        {
            var count = CountOf(translated, span.Token);
            if (count == 0) return new RestoreResult(false, translated, $"The provider dropped or changed a protected term ({span.Original}).");
            if (count > 1) return new RestoreResult(false, translated, $"The provider repeated a protected term ({span.Original}).");
        }
        var any = AnyToken(sent.Prefix);
        foreach (Match m in any.Matches(translated))
            if (!expected.ContainsKey(m.Value)) return new RestoreResult(false, translated, "The provider added a protected-term marker that was never sent.");

        var restored = any.Replace(translated, m => expected.TryGetValue(m.Value, out var span) ? span.Replacement : m.Value);
        return new RestoreResult(true, restored, null);
    }

    /// <summary>The instruction that goes with protected text, for providers that take a prompt.</summary>
    public static string Instruction(ProtectedText sent) =>
        sent.HasTokens
            ? $"The text contains placeholders written like {sent.Spans[0].Token}. Copy every placeholder into the translation exactly as written, once, in the place where its words belong. Never translate, change, split, repeat, reorder or remove a placeholder, and never add one."
            : "";

    private static bool HasBoundary(string text, string term, int start, int end)
    {
        if (char.IsAsciiLetterOrDigit(term[0]) && start > 0 && char.IsAsciiLetterOrDigit(text[start - 1])) return false;
        if (char.IsAsciiLetterOrDigit(term[^1]) && end < text.Length && char.IsAsciiLetterOrDigit(text[end])) return false;
        return true;
    }

    /// <summary>"G" unless the text itself already contains a marker of that shape, in which case letters are added until it does not.</summary>
    private static string ChoosePrefix(string text)
    {
        var prefix = "G";
        while (text.Contains(Open + prefix, StringComparison.Ordinal)) prefix += "G";
        return prefix;
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
