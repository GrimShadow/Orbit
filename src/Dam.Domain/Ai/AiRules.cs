using System.Buffers.Binary;
using System.Text;

namespace Dam.Domain.Ai;

/// <summary>Vectors as stored in <c>asset_embeddings.vector</c>: float32, little-endian, no header (the dimension is its own column).</summary>
public static class EmbeddingCodec
{
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        return bytes;
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0) throw new ArgumentException("A stored vector must be a whole number of 32-bit floats.", nameof(bytes));
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        return vector;
    }
}

public static class VectorMath
{
    /// <summary>Scales to unit length. A zero (or non-finite) vector is returned as zeros: it can never match anything.</summary>
    public static float[] Normalize(IReadOnlyList<float> v)
    {
        double sum = 0;
        for (var i = 0; i < v.Count; i++) sum += (double)v[i] * v[i];
        var result = new float[v.Count];
        if (sum <= 0 || !double.IsFinite(sum)) return result;
        var norm = Math.Sqrt(sum);
        for (var i = 0; i < v.Count; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    /// <summary>The unit-length mean of several vectors of the same dimension (the centroid), or null when there are none.</summary>
    public static float[]? NormalizedMean(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0) return null;
        var dim = vectors[0].Length;
        if (vectors.Any(v => v.Length != dim)) throw new ArgumentException("All vectors must have the same dimension.", nameof(vectors));
        var sum = new double[dim];
        foreach (var v in vectors) for (var i = 0; i < dim; i++) sum[i] += v[i];
        return Normalize(sum.Select(x => (float)(x / vectors.Count)).ToArray());
    }

    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count) throw new ArgumentException("Vectors differ in dimension.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na <= 0 || nb <= 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>OpenSearch scores a cosine-similarity k-NN hit as (1 + cos) / 2; this undoes it.</summary>
    public static double CosineFromKnnScore(double score) => 2 * score - 1;
}

/// <summary>One leg of a hybrid search: its name and the ids it found, best first.</summary>
public sealed record RankedList(string Name, IReadOnlyList<Guid> Ids);

/// <param name="Legs">The names of the lists the item appeared in (in the order the lists were given).</param>
/// <param name="BestRank">The best (lowest, 1-based) rank across the lists it appeared in.</param>
public sealed record FusedItem(Guid Id, double Score, IReadOnlyList<string> Legs, int BestRank);

/// <summary>
/// Reciprocal Rank Fusion (Cormack et al.): an item's score is the sum over the lists it appears in of 1 / (k + rank), rank starting at 1.
/// An item missing from a list simply gets nothing from it. Ties break by the better best-rank, then by id, so the order is deterministic.
/// </summary>
public static class RankFusion
{
    public const int DefaultK = 60;

    public static IReadOnlyList<FusedItem> Fuse(IReadOnlyList<RankedList> lists, int k = DefaultK)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1.");
        var scores = new Dictionary<Guid, (double Score, int Best, List<string> Legs)>();
        foreach (var list in lists)
        {
            var seen = new HashSet<Guid>();
            var rank = 0;
            foreach (var id in list.Ids)
            {
                if (!seen.Add(id)) continue;   // a repeated id keeps its first, better rank
                rank++;
                var add = 1.0 / (k + rank);
                if (scores.TryGetValue(id, out var cur))
                {
                    cur.Legs.Add(list.Name);
                    scores[id] = (cur.Score + add, Math.Min(cur.Best, rank), cur.Legs);
                }
                else scores[id] = (add, rank, [list.Name]);
            }
        }
        return scores.Select(kv => new FusedItem(kv.Key, kv.Value.Score, kv.Value.Legs, kv.Value.Best))
            .OrderByDescending(i => i.Score).ThenBy(i => i.BestRank).ThenBy(i => i.Id).ToList();
    }
}

/// <summary>Turns a provider's candidate tags into the suggestions worth showing: normalised, above the threshold, not already on the asset.</summary>
public static class TagCandidates
{
    public const int MaxLength = 50;

    public static string? Normalize(string? label)
    {
        if (label is null) return null;
        var t = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant().Trim('#', '.', ',', ';', ':', '"', '\'');
        return t.Length is 0 or > MaxLength ? null : t;
    }

    /// <param name="existing">Tags the asset already has, and tags already suggested for this version (in any state): neither is proposed again.</param>
    public static IReadOnlyList<(string Label, float Score)> Select(
        IEnumerable<(string Label, float Score)> candidates, IEnumerable<string> existing, int topK, float threshold)
    {
        var skip = existing.Select(Normalize).Where(t => t is not null).ToHashSet(StringComparer.Ordinal);
        var result = new List<(string, float)>();
        foreach (var (label, score) in candidates.OrderByDescending(c => c.Score))
        {
            if (result.Count >= topK) break;
            if (!float.IsFinite(score) || score < threshold) continue;
            var t = Normalize(label);
            if (t is null || !skip.Add(t)) continue;
            result.Add((t, Math.Clamp(score, 0f, 1f)));
        }
        return result;
    }
}

/// <summary>Cuts text into pieces an embedding model can digest, on sentence or line boundaries where it can.</summary>
public static class TextChunker
{
    public static IReadOnlyList<string> Chunk(string? text, int maxChars = 1000, int maxChunks = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(text) || maxChars <= 0) return [];
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in Pieces(text!, maxChars))
        {
            if (current.Length > 0 && current.Length + 1 + piece.Length > maxChars)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
                if (chunks.Count >= maxChunks) return chunks;
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(piece);
        }
        if (current.Length > 0 && chunks.Count < maxChunks) chunks.Add(current.ToString().Trim());
        return chunks.Where(c => c.Length > 0).ToList();
    }

    /// <summary>Chunks a transcript by whole segments, so a chunk never starts mid-sentence of the speaker.</summary>
    public static IReadOnlyList<string> Chunk(IReadOnlyList<TranscriptSegment> segments, int maxChars = 1000, int maxChunks = int.MaxValue) =>
        Chunk(string.Join(' ', segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0)), maxChars, maxChunks);

    private static IEnumerable<string> Pieces(string text, int maxChars)
    {
        foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var end = line[i] is '.' or '!' or '?' or '。' or '।' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1]));
                if (!end && i - start + 1 < maxChars) continue;
                var piece = line[start..(i + 1)].Trim();
                start = i + 1;
                if (piece.Length > 0) yield return piece;
            }
            if (start < line.Length && line[start..].Trim() is { Length: > 0 } rest) yield return rest;
        }
    }
}

/// <summary>How much of a video's or recording's audio is sent for transcription, and how it is encoded to stay under the upload limit.</summary>
/// <param name="Seconds">At most this many seconds of audio are extracted.</param>
/// <param name="BitrateKbps">Mono 16 kHz MP3 at this bitrate.</param>
public readonly record struct AudioPlan(int Seconds, int BitrateKbps)
{
    /// <summary>The largest upload any provider takes (the self-hosted sidecar and OpenAI both stop at 25 MB); a little is kept in hand.</summary>
    public const long MaxUploadBytes = 24L * 1024 * 1024;
    public const int HardCapMinutes = 600;

    public static AudioPlan For(int maxMinutes)
    {
        var minutes = Math.Clamp(maxMinutes, 1, HardCapMinutes);
        var seconds = minutes * 60;
        // 32 kbps is 4,000 bytes a second: 100 minutes fit in the limit. Longer recordings drop to 24 kbps (3,000 bytes a second, ~133 minutes);
        // beyond that the length is cut so the upload still fits.
        var kbps = seconds <= MaxUploadBytes / 4000 ? 32 : 24;
        var fit = (int)(MaxUploadBytes / (kbps * 125L));
        return new AudioPlan(Math.Min(seconds, fit), kbps);
    }
}

public static class LanguageCodes
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["hindi"] = "hi", ["marathi"] = "mr", ["tamil"] = "ta", ["telugu"] = "te", ["bengali"] = "bn", ["gujarati"] = "gu",
        ["kannada"] = "kn", ["malayalam"] = "ml", ["punjabi"] = "pa", ["urdu"] = "ur", ["german"] = "de", ["french"] = "fr", ["spanish"] = "es",
        ["portuguese"] = "pt", ["italian"] = "it", ["dutch"] = "nl", ["russian"] = "ru", ["japanese"] = "ja", ["chinese"] = "zh", ["korean"] = "ko",
        ["arabic"] = "ar", ["turkish"] = "tr", ["polish"] = "pl", ["swedish"] = "sv", ["thai"] = "th", ["vietnamese"] = "vi", ["indonesian"] = "id",
    };

    /// <summary>A lower-case two- or three-letter code from a code or an English language name ("English" becomes "en"); null when it is neither.</summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var l = language.Trim();
        if (Names.TryGetValue(l, out var code)) return code;
        var primary = l.Split('-', '_')[0].ToLowerInvariant();
        return primary.Length is 2 or 3 && primary.All(char.IsAsciiLetterLower) ? primary : null;
    }
}
