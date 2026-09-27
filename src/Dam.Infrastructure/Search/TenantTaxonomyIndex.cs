using Dam.Domain.Content;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Search;

/// <summary>
/// Every vocabulary and term for the current tenant, loaded once and reused across many documents. A term's code alone
/// is not unique tenant-wide (spec A4.1: codes are unique per vocabulary), so lookups always go by (vocabulary key, code) -
/// the vocabulary key comes from the metadata field's own <see cref="FieldDefinition.Vocabulary"/>.
/// </summary>
public sealed class TenantTaxonomyIndex
{
    private readonly Dictionary<string, Guid> _vocabularyIdByKey;
    private readonly Dictionary<(Guid VocabularyId, string Code), Term> _termsByVocabularyAndCode;

    private TenantTaxonomyIndex(Dictionary<string, Guid> vocabularyIdByKey, Dictionary<(Guid, string), Term> termsByVocabularyAndCode)
    {
        _vocabularyIdByKey = vocabularyIdByKey;
        _termsByVocabularyAndCode = termsByVocabularyAndCode;
    }

    public static async Task<TenantTaxonomyIndex> LoadAsync(DamDbContext db, CancellationToken ct)
    {
        var vocabularies = await db.Vocabularies.AsNoTracking().Select(v => new { v.Id, v.Key }).ToListAsync(ct);
        var terms = await db.Terms.AsNoTracking().ToListAsync(ct);
        return new TenantTaxonomyIndex(
            vocabularies.ToDictionary(v => v.Key, v => v.Id),
            terms.ToDictionary(t => (t.VocabularyId, t.Code), t => t));
    }

    /// <summary>The term for a code selected through a field whose vocabulary key is <paramref name="vocabularyKey"/>,
    /// or null when the vocabulary is gone or the code no longer resolves (e.g. a deprecated term later deleted).</summary>
    public Term? Resolve(string vocabularyKey, string code) =>
        _vocabularyIdByKey.TryGetValue(vocabularyKey, out var vocabId) && _termsByVocabularyAndCode.TryGetValue((vocabId, code), out var term)
            ? term : null;
}
