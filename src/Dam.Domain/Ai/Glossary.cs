using Dam.Domain.Common;

namespace Dam.Domain.Ai;

public static class GlossaryModes
{
    /// <summary>Kept exactly as written in every language (a model name, a brand).</summary>
    public const string Locked = "locked";
    /// <summary>Has a fixed rendering per target language.</summary>
    public const string Mapped = "mapped";
    public static readonly IReadOnlyList<string> All = [Locked, Mapped];
}

public sealed record GlossaryTermCreated(Guid TermId, string Term, string Mode) : DomainEvent, IEntityEvent
{
    public const string EventName = "glossary.term.created";
    public override string Name => EventName;
    public string EntityType => "glossary_term";
    public Guid EntityId => TermId;
}

public sealed record GlossaryTermUpdated(Guid TermId, string Term, string Mode) : DomainEvent, IEntityEvent
{
    public const string EventName = "glossary.term.updated";
    public override string Name => EventName;
    public string EntityType => "glossary_term";
    public Guid EntityId => TermId;
}

public sealed record GlossaryTermDeleted(Guid TermId, string Term) : DomainEvent, IEntityEvent
{
    public const string EventName = "glossary.term.deleted";
    public override string Name => EventName;
    public string EntityType => "glossary_term";
    public Guid EntityId => TermId;
}

/// <summary>
/// A word or phrase a tenant never wants a machine to translate freely (ADR-0036): a product name, a brand, a technical term. <c>locked</c> terms stay as
/// written in every language; <c>mapped</c> terms are replaced by a fixed rendering for each target language that has one. Applied by
/// <see cref="GlossaryProtector"/>, so it holds whichever AI provider does the translating.
/// </summary>
public sealed class GlossaryTerm : AggregateRoot
{
    private GlossaryTerm() { }

    public string Term { get; private set; } = "";
    public string Mode { get; private set; } = GlossaryModes.Locked;
    /// <summary>Language code to rendering; empty for a locked term.</summary>
    public Dictionary<string, string> Translations { get; private set; } = [];
    public string Note { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static GlossaryTerm Create(Guid tenantId, string term, string mode, IReadOnlyDictionary<string, string>? translations, string? note, DateTimeOffset now)
    {
        var t = new GlossaryTerm { TenantId = tenantId, CreatedAt = now, UpdatedAt = now };
        t.Apply(term, mode, translations, note);
        t.Raise(new GlossaryTermCreated(t.Id, t.Term, t.Mode));
        return t;
    }

    public void Update(string term, string mode, IReadOnlyDictionary<string, string>? translations, string? note, DateTimeOffset now)
    {
        Apply(term, mode, translations, note);
        UpdatedAt = now;
        Raise(new GlossaryTermUpdated(Id, Term, Mode));
    }

    public void MarkDeleted() => Raise(new GlossaryTermDeleted(Id, Term));

    private void Apply(string term, string mode, IReadOnlyDictionary<string, string>? translations, string? note)
    {
        Term = term.Trim();
        Mode = mode;
        Translations = mode == GlossaryModes.Mapped && translations is not null
            ? translations.ToDictionary(kv => kv.Key.Trim().ToLowerInvariant(), kv => kv.Value.Trim())
            : [];
        Note = note?.Trim() ?? "";
    }

    public GlossaryEntry ToEntry() => new(Term, Mode, Translations);
}

/// <summary>What the protector needs to know of a term.</summary>
public sealed record GlossaryEntry(string Term, string Mode, IReadOnlyDictionary<string, string> Translations)
{
    /// <summary>What the term becomes in <paramref name="language"/>: its rendering when mapped and one exists (for the exact code, else its primary language), otherwise the term as written.</summary>
    public string RenderIn(string language)
    {
        if (Mode != GlossaryModes.Mapped) return Term;
        var wanted = language.Trim().ToLowerInvariant();
        foreach (var key in new[] { wanted, wanted.Split('-')[0] })
            foreach (var (k, v) in Translations)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && v.Length > 0) return v;
        return Term;
    }
}
