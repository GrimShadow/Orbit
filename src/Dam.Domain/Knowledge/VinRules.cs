using Dam.Domain.Common;

namespace Dam.Domain.Knowledge;

public sealed record VinRuleCreated(Guid RuleId, string RuleName, string Pattern, string Model, string? Variant) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.vin_rule.created";
    public override string Name => EventName;
    public string EntityType => "vin_rule";
    public Guid EntityId => RuleId;
}

public sealed record VinRuleUpdated(Guid RuleId, string RuleName, string Pattern, string Model, string? Variant, bool Enabled) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.vin_rule.updated";
    public override string Name => EventName;
    public string EntityType => "vin_rule";
    public Guid EntityId => RuleId;
}

public sealed record VinRuleDeleted(Guid RuleId, string RuleName) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.vin_rule.deleted";
    public override string Name => EventName;
    public string EntityType => "vin_rule";
    public Guid EntityId => RuleId;
}

/// <summary>A CSV of rules was applied (or, with <paramref name="Partial"/>, applied except for its invalid lines). One event for the whole import, not one per rule.</summary>
public sealed record VinRulesImported(int Created, int Updated, int Skipped, int Rejected, bool Partial) : DomainEvent
{
    public const string EventName = "knowledge.vin_rules.imported";
    public override string Name => EventName;
}

/// <summary>What a rule says about a vehicle, apart from its id.</summary>
public sealed record RuleFacts(string Pattern, string Model, string? Variant, int? ModelYear, bool YearFromVin, string? Theme, int Priority, bool Enabled)
{
    public bool SameAs(VinRule r) =>
        Pattern == r.Pattern && Model == r.Model && Variant == r.Variant && ModelYear == r.ModelYear && (ModelYear is null && YearFromVin) == r.YearFromVin
        && Theme == r.Theme && Priority == r.Priority && Enabled == r.Enabled;
}

/// <summary>
/// A VIN rule (ADR-0039): a pattern that says "a vehicle whose VIN looks like this is this model (and variant, and year, and theme)". Rules are tenant data;
/// no customer's models or VIN prefixes live in code.
/// </summary>
public sealed class VinRule : AggregateRoot
{
    private VinRule() { }

    public string Name { get; private set; } = "";
    public string Pattern { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string? Variant { get; private set; }
    /// <summary>A fixed model year; when set it wins over the VIN's tenth character.</summary>
    public int? ModelYear { get; private set; }
    /// <summary>Read the model year from the VIN's tenth character.</summary>
    public bool YearFromVin { get; private set; } = true;
    public string? Theme { get; private set; }
    /// <summary>Higher wins when several rules match one VIN.</summary>
    public int Priority { get; private set; }
    public bool Enabled { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a rule. <paramref name="quiet"/> keeps it from raising its own event (a CSV import raises one event for all of its rules).</summary>
    public static VinRule Create(Guid tenantId, string name, RuleFacts f, DateTimeOffset now, bool quiet = false)
    {
        var r = new VinRule { TenantId = tenantId, Name = name.Trim(), CreatedAt = now, UpdatedAt = now };
        r.Apply(f);
        if (!quiet) r.Raise(new VinRuleCreated(r.Id, r.Name, r.Pattern, r.Model, r.Variant));
        return r;
    }

    public void Update(string name, RuleFacts f, DateTimeOffset now, bool quiet = false)
    {
        Name = name.Trim();
        Apply(f);
        UpdatedAt = now;
        if (!quiet) Raise(new VinRuleUpdated(Id, Name, Pattern, Model, Variant, Enabled));
    }

    public void MarkDeleted() => Raise(new VinRuleDeleted(Id, Name));

    private void Apply(RuleFacts f)
    {
        Pattern = f.Pattern;
        Model = f.Model;
        Variant = f.Variant;
        ModelYear = f.ModelYear;
        YearFromVin = f.ModelYear is null && f.YearFromVin;
        Theme = f.Theme;
        Priority = f.Priority;
        Enabled = f.Enabled;
    }
}

/// <summary>A rule ready to be matched: its pattern parsed.</summary>
public sealed record VinRuleView(Guid Id, string Name, VinPattern Pattern, string Model, string? Variant, int? ModelYear, bool YearFromVin, string? Theme, int Priority, bool Enabled)
{
    /// <summary>The rule as a view, or null when its stored pattern no longer parses (it cannot match anything then).</summary>
    public static VinRuleView? From(VinRule r) =>
        VinPattern.TryParse(r.Pattern, out var p, out _) ? new VinRuleView(r.Id, r.Name, p, r.Model, r.Variant, r.ModelYear, r.YearFromVin, r.Theme, r.Priority, r.Enabled) : null;

    public int Specificity => Pattern.Specificity;
}

/// <summary>What a rule makes of one VIN.</summary>
public sealed record Vehicle(string Model, string? Variant, int? ModelYear, string? Theme);

/// <summary>One rule weighed against a VIN, for the rule tester.</summary>
public sealed record RuleEvaluation(VinRuleView Rule, bool Matched, string Reason);

/// <summary>Rule choice (ADR-0039 decision 3): among enabled rules that match, the highest priority, then the most literal characters, then the smallest id.</summary>
public static class VinRuleSelector
{
    public static VinRuleView? Choose(IEnumerable<VinRuleView> rules, string vin) =>
        rules.Where(r => r.Enabled && r.Pattern.Matches(vin)).OrderBy(r => r, Comparer).FirstOrDefault();

    /// <summary>Every rule weighed against the VIN, the chosen one marked, in the order they would be chosen (matching rules first).</summary>
    public static IReadOnlyList<RuleEvaluation> Evaluate(IEnumerable<VinRuleView> rules, string vin)
    {
        var rows = rules.Select(r =>
        {
            if (!r.Enabled) return new RuleEvaluation(r, false, "The rule is switched off.");
            var why = r.Pattern.WhyNot(vin);
            return why is null
                ? new RuleEvaluation(r, true, $"Matches (priority {r.Priority}, {r.Specificity} fixed characters).")
                : new RuleEvaluation(r, false, why);
        }).ToList();
        var matched = rows.Where(x => x.Matched).OrderBy(x => x.Rule, Comparer).ToList();
        var rest = rows.Where(x => !x.Matched).OrderBy(x => x.Rule.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Rule.Id, IdComparer.Instance).ToList();
        if (matched.Count > 1)
            matched = matched.Select((x, i) => i == 0 ? x : x with { Reason = x.Reason + " Not chosen: " + WhyLower(matched[0].Rule, x.Rule) }).ToList();
        return [.. matched, .. rest];
    }

    private static string WhyLower(VinRuleView winner, VinRuleView loser)
    {
        if (winner.Priority != loser.Priority) return $"'{winner.Name}' has a higher priority.";
        if (winner.Specificity != loser.Specificity) return $"'{winner.Name}' is more specific.";
        return $"'{winner.Name}' comes first when priority and specificity tie.";
    }

    /// <summary>Applies a rule to a VIN. <paramref name="currentYear"/> only matters when the rule reads the year from the VIN.</summary>
    public static Vehicle VehicleFor(VinRuleView rule, string vin, int currentYear)
    {
        var year = rule.ModelYear ?? (rule.YearFromVin ? ModelYears.FromVin(vin, currentYear) : null);
        return new Vehicle(rule.Model, rule.Variant, year, rule.Theme);
    }

    private static readonly IComparer<VinRuleView> Comparer = System.Collections.Generic.Comparer<VinRuleView>.Create((a, b) =>
    {
        var c = b.Priority.CompareTo(a.Priority);
        if (c != 0) return c;
        c = b.Specificity.CompareTo(a.Specificity);
        return c != 0 ? c : IdComparer.Instance.Compare(a.Id, b.Id);
    });
}

/// <summary>Orders ids the way their canonical (big-endian) bytes sort, which for UUID v7 is oldest first, and the way Postgres sorts uuids.</summary>
public sealed class IdComparer : IComparer<Guid>
{
    public static readonly IdComparer Instance = new();

    public int Compare(Guid a, Guid b) => a.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(b.ToByteArray(bigEndian: true));
}
