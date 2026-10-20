using System.Globalization;
using System.Text;

namespace Dam.Domain.Ai;

/// <summary>
/// A summary made by choosing the most representative sentences of the text itself (ADR-0036): no model, no network, so the default self-hosted deployment
/// summarises documents offline and nothing leaves the installation. Sentences are scored by how many of the text's frequent words they contain
/// (stop words and one-letter words do not count), with a small bonus for the opening ones, the best are taken until the word budget is spent, and they
/// are returned in their original order. Works for any script: words are runs of letters, digits and combining marks (so Devanagari vowel signs stay
/// inside their word), and sentences end at <c>. ! ? 。</c> (when followed by space or the end), at the danda <c>।</c> and double danda <c>॥</c>, and at line breaks.
/// </summary>
public static class ExtractiveSummary
{
    public const string ModelName = "extractive-v1";
    public const int DefaultMaxWords = 120;

    // Small on purpose: enough to stop "the" and "है" from carrying a sentence, not a linguistic resource.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "were", "with", "that", "this", "from", "have", "has", "had", "not", "but", "you", "your", "our", "its", "their",
        "will", "can", "all", "any", "may", "than", "then", "there", "which", "who", "what", "when", "where", "into", "such", "been", "being", "also", "more",
        "of", "is", "in", "to", "on", "at", "it", "be", "by", "as", "or", "an", "if", "so", "do", "we", "he", "no", "up", "my", "us", "am", "she", "his", "her",
        "का", "की", "के", "है", "हैं", "में", "और", "से", "को", "पर", "यह", "यहाँ", "था", "थी", "थे", "भी", "कि", "तो", "एक", "ने", "हो", "या", "इस", "उस",
    };

    /// <summary>Whole text if it already fits in <paramref name="maxWords"/> words (repeated sentences removed); otherwise the best sentences, in order.</summary>
    public static string Summarise(string? text, int maxWords = DefaultMaxWords)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        maxWords = Math.Clamp(maxWords, 1, 2000);

        var sentences = Sentences(text).ToList();
        if (sentences.Count == 0) return "";
        // The same line on every page of a scan (a running header) is said once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sentences = sentences.Where(s => seen.Add(s)).ToList();

        var counts = sentences.Select(WordCount).ToList();
        if (counts.Sum() <= maxWords) return string.Join(' ', sentences);

        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var words = sentences.Select(s => Words(s).ToList()).ToList();
        foreach (var w in words.SelectMany(x => x).Where(IsContentWord)) frequency[w] = frequency.GetValueOrDefault(w) + 1;

        var scored = new List<(int Index, double Score)>();
        for (var i = 0; i < sentences.Count; i++)
        {
            var content = words[i].Where(IsContentWord).ToList();
            // Normalised by the square root of the length: longer sentences are not favoured outright, and not punished either.
            var score = content.Count == 0 ? 0 : content.Sum(w => (double)frequency[w]) / Math.Sqrt(content.Count);
            if (i < 3) score *= 1.0 + 0.15 * (3 - i);   // openings state what a document is about
            scored.Add((i, score));
        }

        var chosen = new SortedSet<int>();
        var budget = maxWords;
        foreach (var (index, _) in scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index))
        {
            if (counts[index] > budget) continue;
            chosen.Add(index);
            budget -= counts[index];
            if (budget <= 0) break;
        }
        if (chosen.Count == 0)
        {
            // Even the best sentence is longer than the budget: cut it at a word.
            var best = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index).First().Index;
            return CutAtWords(sentences[best], maxWords);
        }
        return string.Join(' ', chosen.Select(i => sentences[i]));
    }

    /// <summary>Splits into sentences, keeping their punctuation. Blank pieces are dropped.</summary>
    public static IEnumerable<string> Sentences(string text)
    {
        foreach (var line in text.Split(['\n', '\r', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var ends = c is '।' or '॥'
                    || (c is '.' or '!' or '?' or '。' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])));
                if (!ends) continue;
                // Several marks in a row ("?!", "...") belong to the same sentence.
                while (i + 1 < line.Length && line[i + 1] is '.' or '!' or '?' or '।' or '॥') i++;
                var piece = line[start..(i + 1)].Trim();
                start = i + 1;
                if (piece.Any(IsWordChar)) yield return piece;
            }
            if (start < line.Length && line[start..].Trim() is { Length: > 0 } rest && rest.Any(IsWordChar)) yield return rest;
        }
    }

    public static int WordCount(string text) => Words(text).Count();

    /// <summary>Words in order: runs of letters, digits and combining marks, lower-cased.</summary>
    public static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (IsWordChar(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private static bool IsContentWord(string w) => w.Length > 1 && !StopWords.Contains(w);

    private static string CutAtWords(string sentence, int maxWords)
    {
        var count = 0;
        var inWord = false;
        for (var i = 0; i < sentence.Length; i++)
        {
            var word = IsWordChar(sentence[i]);
            if (word && !inWord && ++count > maxWords) return sentence[..i].TrimEnd() + "…";
            inWord = word;
        }
        return sentence;
    }
}
