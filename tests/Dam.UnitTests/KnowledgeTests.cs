using Dam.Application.Ai;
using Dam.Application.Knowledge;
using Dam.Domain.Authorization;
using Dam.Domain.Knowledge;
using Dam.Shared.Knowledge;

namespace Dam.UnitTests;

/// <summary>The VIN, the pattern language and the model-year code (ADR-0039): pure, so every rule is pinned here.</summary>
public sealed class VinPatternTests
{
    // A fictional VIN: 17 characters, none of I, O or Q.
    private const string Vin1 = "ACMRS1AB4RA000123";

    private static VinPattern Parse(string text)
    {
        Assert.True(VinPattern.TryParse(text, out var p, out var error), error);
        return p;
    }

    private static string Reject(string? text)
    {
        Assert.False(VinPattern.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
        return error;
    }

    // ---- the VIN --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ACMRS1AB4RA000123", "ACMRS1AB4RA000123")]
    [InlineData("  acmrs1ab4ra000123  ", "ACMRS1AB4RA000123")]
    [InlineData("11111111111111111", "11111111111111111")]
    public void A_vin_is_trimmed_and_upper_cased(string input, string expected)
    {
        Assert.True(Vin.TryNormalize(input, out var vin, out _));
        Assert.Equal(expected, vin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ACMRS1AB4RA00012")]          // 16
    [InlineData("ACMRS1AB4RA0001234")]        // 18
    [InlineData("ACMRS1AB4RA00012I")]         // I
    [InlineData("ACMRS1AB4RA00012O")]         // O
    [InlineData("ACMRS1AB4RA00012Q")]         // Q
    [InlineData("ACMRS1AB4RA00012-")]         // punctuation
    [InlineData("ACMRS1AB4RA 00123")]         // inner space
    [InlineData("ACMRS1AB4RA00012é")]         // not ASCII
    public void A_malformed_vin_is_refused_with_a_reason(string? input)
    {
        Assert.False(Vin.TryNormalize(input, out var vin, out var error));
        Assert.Equal("", vin);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_reason_names_the_problem()
    {
        Vin.TryNormalize("ACMRS1AB4RA00012", out _, out var tooShort);
        Assert.Contains("17", tooShort, StringComparison.Ordinal);
        Assert.Contains("16", tooShort, StringComparison.Ordinal);
        Vin.TryNormalize("ACMRS1AB4RA00012I", out _, out var forbidden);
        Assert.Contains("I, O or Q", forbidden, StringComparison.Ordinal);
        Vin.TryNormalize("ACMRS1AB4RA 00123", out _, out var space);
        Assert.Contains("letters and digits", space, StringComparison.Ordinal);
    }

    // ---- the pattern: what matches --------------------------------------------------------------------------

    [Theory]
    [InlineData("ACM*", true)]                          // a prefix
    [InlineData("acm*", true)]                          // case does not matter
    [InlineData("AC*", true)]
    [InlineData("A*", true)]
    [InlineData("ACN*", false)]
    [InlineData("CM*", false)]                          // anchored at the first character
    [InlineData("ACMRS1AB4RA000123", true)]             // the whole VIN
    [InlineData("acmrs1ab4ra000123", true)]
    [InlineData("ACMRS1AB4RA000124", false)]
    [InlineData("ACMRS1???????????", true)]             // 6 fixed characters then 11 of anything
    [InlineData("ACMRS2???????????", false)]
    [InlineData("ACM??1*", true)]                       // ? is exactly one character
    [InlineData("ACM??2*", false)]
    [InlineData("?CM*", true)]
    [InlineData("AC[MN]*", true)]                       // a set
    [InlineData("AC[NP]*", false)]
    [InlineData("AC[A-F]*", false)]
    [InlineData("AC[K-Z]*", true)]                      // a range
    [InlineData("AC[A-CM]*", true)]                     // a range and a single character
    [InlineData("ACMRS[0-9]*", true)]                   // a digit range
    [InlineData("ACMRS[2-9]*", false)]
    [InlineData("[A-C][A-C][L-N]*", true)]
    [InlineData("ACMRS1AB4RA[0-9][0-9][0-9][0-9][0-9][0-9]", true)]
    [InlineData("ACMRS1AB4RA??????", true)]
    [InlineData("?????????????????", true)]             // 17 ?
    [InlineData("?????????????????*", true)]            // 17 ? and a star
    public void A_pattern_matches_as_the_language_says(string pattern, bool expected)
    {
        Assert.Equal(expected, Parse(pattern).Matches(Vin1));
    }

    [Fact]
    public void A_pattern_without_a_star_matches_only_the_exact_length_and_a_vin_of_another_length_never_matches()
    {
        Assert.False(Parse("ACM*").Matches("ACMRS1AB4RA00012"));        // 16 characters
        Assert.False(Parse("ACM*").Matches(""));
        Assert.False(Parse("?????????????????").Matches("ACMRS1AB4RA0001234"));
    }

    [Fact]
    public void The_text_is_stored_upper_cased_and_trimmed()
    {
        var p = Parse("  acm[mn]*  ");
        Assert.Equal("ACM[MN]*", p.Text);
        Assert.True(p.IsOpen);
        Assert.Equal(4, p.Length);
        Assert.Equal("ACM[MN]*", p.ToString());
    }

    [Theory]
    [InlineData("ACM*", 3)]
    [InlineData("AC?*", 2)]
    [InlineData("AC[MN]*", 2)]                          // a set is not a fixed character
    [InlineData("ACMRS1AB4RA000123", 17)]
    [InlineData("?????????????????", 0)]
    [InlineData("ACM??1*", 4)]
    public void Specificity_counts_the_fixed_characters(string pattern, int expected)
    {
        Assert.Equal(expected, Parse(pattern).Specificity);
    }

    [Fact]
    public void Why_not_names_the_first_character_that_disagrees()
    {
        Assert.Null(Parse("ACM*").WhyNot(Vin1));
        var why = Parse("ACN*").WhyNot(Vin1)!;
        Assert.Contains("Character 3", why, StringComparison.Ordinal);
        Assert.Contains("'M'", why, StringComparison.Ordinal);
        Assert.Contains("'N'", why, StringComparison.Ordinal);
        Assert.Contains("one of MN", Parse("AC[MN]*").WhyNot("ACZ" + Vin1[3..])!, StringComparison.Ordinal);
        Assert.NotNull(Parse("ACM*").WhyNot("SHORT"));
    }

    // ---- the pattern: what is refused ------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ACM")]                                 // too short without a star
    [InlineData("ACMRS1AB4RA00012")]                    // 16, no star
    [InlineData("*")]                                   // nothing before the star
    [InlineData("AC*M")]                                // star not last
    [InlineData("AC**")]
    [InlineData("ACMI*")]                               // I
    [InlineData("ACMO*")]
    [InlineData("ACMQ*")]
    [InlineData("AC-*")]                                // punctuation
    [InlineData("AC M*")]                               // a space inside
    [InlineData("AC$*")]
    [InlineData("AC[MN*")]                              // unclosed set
    [InlineData("AC]*")]                                // stray ]
    [InlineData("AC[]*")]                               // empty set
    [InlineData("AC[^M]*")]                             // negation is not supported
    [InlineData("AC[N-M]*")]                            // backwards range
    [InlineData("AC[A-9]*")]                            // a range across letters and digits
    [InlineData("AC[M-]*")]                             // dangling dash
    [InlineData("AC[-M]*")]
    [InlineData("AC[IOQ]*")]                            // a set that can match nothing
    [InlineData("AC[I]*")]
    [InlineData("AC[M?]*")]
    [InlineData("AC[[M]*")]
    [InlineData("??????????????????*")]                 // 18 positions
    [InlineData("??????????????????")]
    public void An_invalid_pattern_is_refused_with_a_message(string? pattern)
    {
        Reject(pattern);
    }

    [Fact]
    public void An_over_long_pattern_text_is_refused()
    {
        Reject(new string('A', VinPattern.MaxTextLength + 1));
    }

    [Fact]
    public void Messages_say_what_to_do()
    {
        Assert.Contains("Add a *", Reject("ACM"), StringComparison.Ordinal);
        Assert.Contains("last character", Reject("AC*M"), StringComparison.Ordinal);
        Assert.Contains("I, O and Q", Reject("ACMI*"), StringComparison.Ordinal);
        Assert.Contains("never closed", Reject("AC[MN*"), StringComparison.Ordinal);
        Assert.Contains("backwards", Reject("AC[N-M]*"), StringComparison.Ordinal);
        Assert.Contains("Negated", Reject("AC[^M]*"), StringComparison.Ordinal);
        Assert.Contains("at most 17", Reject("??????????????????*"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_that_includes_forbidden_letters_just_skips_them()
    {
        var p = Parse("[H-K]??????????????*");                  // H, J, K are possible; I is not
        Assert.True(p.Matches("H" + new string('1', 16)));
        Assert.True(p.Matches("J" + new string('1', 16)));
        Assert.False(p.Matches("I" + new string('1', 16)));       // (not a valid VIN anyway)
        Assert.False(p.Matches("L" + new string('1', 16)));
    }
}

public sealed class ModelYearTests
{
    [Theory]
    [InlineData('T', 2026, 2026)]       // T is 1996 and 2026
    [InlineData('V', 2026, 2027)]       // one year ahead is allowed
    [InlineData('W', 2026, 1998)]       // two years ahead is not: the earlier cycle
    [InlineData('X', 2026, 1999)]
    [InlineData('Y', 2026, 2000)]
    [InlineData('A', 2026, 2010)]
    [InlineData('B', 2026, 2011)]
    [InlineData('H', 2026, 2017)]
    [InlineData('J', 2026, 2018)]       // I is skipped
    [InlineData('N', 2026, 2022)]
    [InlineData('P', 2026, 2023)]       // O is skipped
    [InlineData('R', 2026, 2024)]       // Q is skipped
    [InlineData('S', 2026, 2025)]
    [InlineData('1', 2026, 2001)]
    [InlineData('9', 2026, 2009)]
    [InlineData('a', 2026, 2010)]       // case does not matter
    [InlineData('Y', 2029, 2030)]       // 2030 is exactly one year ahead of 2029
    [InlineData('Y', 2028, 2000)]
    [InlineData('1', 2030, 2031)]
    [InlineData('A', 2040, 2040)]
    [InlineData('A', 2010, 2010)]
    [InlineData('A', 2009, 2010)]       // 2010 is one year ahead of 2009
    [InlineData('A', 2008, 1980)]
    public void The_code_names_the_latest_year_not_more_than_one_ahead(char code, int now, int expected)
    {
        Assert.Equal(expected, ModelYears.Decode(code, now));
    }

    [Theory]
    [InlineData('U')]
    [InlineData('Z')]
    [InlineData('0')]
    [InlineData('I')]
    [InlineData('O')]
    [InlineData('Q')]
    [InlineData('-')]
    [InlineData(' ')]
    public void Characters_that_are_not_year_codes_decode_to_nothing(char code)
    {
        Assert.Null(ModelYears.Decode(code, 2026));
    }

    [Fact]
    public void All_thirty_codes_are_distinct_and_cover_a_thirty_year_span()
    {
        Assert.Equal(ModelYears.Cycle, ModelYears.Codes.Length);
        Assert.Equal(ModelYears.Cycle, ModelYears.Codes.Distinct().Count());
        var years = ModelYears.Codes.Select(c => ModelYears.Decode(c, 2026)!.Value).ToList();
        Assert.Equal(ModelYears.Cycle, years.Distinct().Count());
        Assert.All(years, y => Assert.InRange(y, 2027 - ModelYears.Cycle + 1, 2027));
    }

    [Fact]
    public void The_year_is_read_from_the_tenth_character_of_the_vin()
    {
        Assert.Equal(2026, ModelYears.FromVin("ACMRS1AB4TA000123", 2026));
        Assert.Equal(2024, ModelYears.FromVin("ACMRS1AB4RA000123", 2026));
        Assert.Null(ModelYears.FromVin("ACMRS1AB4ZA000123", 2026));      // Z is not a year code
        Assert.Null(ModelYears.FromVin("SHORT", 2026));
    }
}

public sealed class VinRuleSelectionTests
{
    private static readonly Guid Low = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid High = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private const string Vin1 = "ACMRS1AB4RA000123";

    private static VinRuleView Rule(string name, string pattern, string model = "roadster", int priority = 0, bool enabled = true, Guid? id = null,
        string? variant = null, int? year = null, bool fromVin = true, string? theme = null)
    {
        Assert.True(VinPattern.TryParse(pattern, out var p, out var e), e);
        return new VinRuleView(id ?? Guid.NewGuid(), name, p, model, variant, year, fromVin, theme, priority, enabled);
    }

    [Fact]
    public void The_highest_priority_wins_over_a_more_specific_pattern()
    {
        var chosen = VinRuleSelector.Choose([Rule("specific", "ACMRS1AB4RA000123"), Rule("broad", "ACM*", priority: 5)], Vin1);
        Assert.Equal("broad", chosen!.Name);
    }

    [Fact]
    public void At_equal_priority_the_pattern_with_more_fixed_characters_wins()
    {
        var chosen = VinRuleSelector.Choose([Rule("a", "AC*"), Rule("b", "ACMRS*"), Rule("c", "ACM*")], Vin1);
        Assert.Equal("b", chosen!.Name);
    }

    [Fact]
    public void A_set_counts_less_than_a_fixed_character()
    {
        var chosen = VinRuleSelector.Choose([Rule("set", "ACMR[SX]*"), Rule("fixed", "ACMRS*")], Vin1);
        Assert.Equal("fixed", chosen!.Name);
    }

    [Fact]
    public void At_equal_priority_and_specificity_the_smallest_id_wins()
    {
        Assert.Equal("first", VinRuleSelector.Choose([Rule("second", "ACM*", id: High), Rule("first", "ACM*", id: Low)], Vin1)!.Name);
        Assert.Equal("first", VinRuleSelector.Choose([Rule("first", "ACM*", id: Low), Rule("second", "ACM*", id: High)], Vin1)!.Name);
    }

    [Fact]
    public void A_negative_priority_loses_to_the_default()
    {
        Assert.Equal("normal", VinRuleSelector.Choose([Rule("fallback", "ACMRS1AB4RA000123", priority: -1), Rule("normal", "AC*")], Vin1)!.Name);
    }

    [Fact]
    public void Disabled_and_non_matching_rules_are_never_chosen()
    {
        Assert.Null(VinRuleSelector.Choose([Rule("off", "ACM*", enabled: false), Rule("other", "XYZ*")], Vin1));
        Assert.Equal("on", VinRuleSelector.Choose([Rule("off", "ACM*", priority: 99, enabled: false), Rule("on", "AC*")], Vin1)!.Name);
        Assert.Null(VinRuleSelector.Choose([], Vin1));
    }

    [Fact]
    public void The_vehicle_takes_a_fixed_year_over_the_vin_and_reads_the_vin_otherwise()
    {
        var fromVin = VinRuleSelector.VehicleFor(Rule("a", "ACM*", variant: "gt", theme: "ev"), Vin1, 2026);
        Assert.Equal(new Vehicle("roadster", "gt", 2024, "ev"), fromVin);
        var fixedYear = VinRuleSelector.VehicleFor(Rule("b", "ACM*", year: 2019, fromVin: false), Vin1, 2026);
        Assert.Equal(2019, fixedYear.ModelYear);
        var none = VinRuleSelector.VehicleFor(Rule("c", "ACM*", fromVin: false), Vin1, 2026);
        Assert.Null(none.ModelYear);
        var badCode = VinRuleSelector.VehicleFor(Rule("d", "ACM*"), "ACMRS1AB4ZA000123", 2026);
        Assert.Null(badCode.ModelYear);
    }

    [Fact]
    public void The_tester_lists_every_rule_with_the_chosen_one_first_and_a_reason_for_each()
    {
        var rules = new[]
        {
            Rule("no match", "XYZ*"), Rule("winner", "ACMRS*", priority: 2), Rule("loser", "ACM*", priority: 1), Rule("switched off", "ACM*", priority: 9, enabled: false),
        };
        var rows = VinRuleSelector.Evaluate(rules, Vin1);
        Assert.Equal(4, rows.Count);
        Assert.Equal(["winner", "loser"], rows.Where(r => r.Matched).Select(r => r.Rule.Name));
        Assert.Equal("winner", rows[0].Rule.Name);
        Assert.Contains("higher priority", rows[1].Reason, StringComparison.Ordinal);
        Assert.Contains("Character 1", rows.Single(r => r.Rule.Name == "no match").Reason, StringComparison.Ordinal);
        Assert.Contains("switched off", rows.Single(r => r.Rule.Name == "switched off").Reason, StringComparison.Ordinal);
        Assert.Equal(VinRuleSelector.Choose(rules, Vin1)!.Name, rows[0].Rule.Name);
    }

    [Fact]
    public void A_stored_pattern_that_no_longer_parses_is_left_out()
    {
        Assert.Null(VinRuleView.From(DraftRule("broken[")));
        Assert.NotNull(VinRuleView.From(DraftRule("ACM*")));
    }

    private static VinRule DraftRule(string pattern) =>
        VinRule.Create(Guid.NewGuid(), "r", new RuleFacts(pattern, "roadster", null, null, true, null, 0, true), DateTimeOffset.UnixEpoch, quiet: true);
}

public sealed class DocumentRankingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DocumentCandidate Doc(string name, string? variant = null, int? from = null, int? to = null, string lang = "en", string status = AssetStatuses.Published,
        int ageDays = 0, byte id = 1) =>
        new(Guid.Parse($"00000000-0000-7000-8000-0000000000{id:x2}"), Guid.NewGuid(), variant, from, to ?? from, lang, status, T0.AddDays(-ageDays));

    private static string Best(IEnumerable<DocumentCandidate> docs, string? variant = "gt", int? year = 2024, string? lang = null, string? def = null) =>
        DocumentRanking.Select(docs, variant, year, lang, def)!.Chosen.DocumentId.ToString()[^2..];

    [Fact]
    public void Published_beats_in_review_whatever_else_differs()
    {
        var review = Doc("r", variant: "gt", from: 2024, id: 1) with { AssetStatus = AssetStatuses.InReview };
        var published = Doc("p", id: 2);                                  // no variant, no year: far less specific
        Assert.Equal("02", Best([review, published]));
        Assert.Equal("01", Best([review]));                               // in review is returned when nothing is published
    }

    [Fact]
    public void An_exact_variant_beats_an_empty_one()
    {
        Assert.Equal("02", Best([Doc("any", id: 1), Doc("gt", variant: "gt", id: 2)]));
        Assert.Equal("01", Best([Doc("any", id: 1), Doc("gt", variant: "gt", id: 2)], variant: "base"));      // the exact one does not fit another variant
        Assert.Equal("01", Best([Doc("any", id: 1)], variant: null));
    }

    [Fact]
    public void A_narrower_year_range_beats_a_wider_one_beats_none()
    {
        var wide = Doc("wide", from: 2020, to: 2030, id: 1);
        var narrow = Doc("narrow", from: 2023, to: 2025, id: 2);
        var single = Doc("single", from: 2024, to: 2024, id: 3);
        var none = Doc("none", id: 4);
        Assert.Equal("03", Best([wide, narrow, single, none]));
        Assert.Equal("02", Best([wide, narrow, none]));
        Assert.Equal("01", Best([wide, none]));
        Assert.Equal("04", Best([wide, narrow, none], year: 2010));       // none of the ranges contains 2010
    }

    [Fact]
    public void A_vehicle_with_no_known_year_only_fits_documents_with_no_year()
    {
        Assert.Equal("02", Best([Doc("ranged", from: 2020, to: 2030, id: 1), Doc("open", id: 2)], year: null));
        Assert.Null(DocumentRanking.Select([Doc("ranged", from: 2020, to: 2030)], "gt", null, null, null));
    }

    [Fact]
    public void Variant_outranks_year_in_the_order_of_the_keys()
    {
        var exactVariantWide = Doc("a", variant: "gt", from: 2020, to: 2030, id: 1);
        var emptyVariantNarrow = Doc("b", from: 2024, to: 2024, id: 2);
        Assert.Equal("01", Best([exactVariantWide, emptyVariantNarrow]));
    }

    [Fact]
    public void Newer_beats_older_and_then_the_smallest_id_decides()
    {
        Assert.Equal("02", Best([Doc("old", ageDays: 10, id: 1), Doc("new", ageDays: 1, id: 2)]));
        Assert.Equal("01", Best([Doc("a", id: 1), Doc("b", id: 2)]));                                      // identical in everything else
        Assert.Equal("01", Best([Doc("b", id: 2), Doc("a", id: 1)]));
    }

    [Fact]
    public void Statuses_other_than_published_and_in_review_never_resolve()
    {
        foreach (var status in new[] { AssetStatuses.Draft, AssetStatuses.Approved, AssetStatuses.Expired, AssetStatuses.Archived, AssetStatuses.Deleted, AssetStatuses.Blocked })
            Assert.Null(DocumentRanking.Select([Doc("x", status: status)], "gt", 2024, null, null));
        Assert.True(DocumentRanking.Resolvable(AssetStatuses.Published));
        Assert.True(DocumentRanking.Resolvable(AssetStatuses.InReview));
    }

    [Fact]
    public void The_language_is_the_one_asked_for_else_the_tenant_default_else_english_else_the_best()
    {
        var en = Doc("en", lang: "en", id: 1);
        var hi = Doc("hi", lang: "hi", id: 2);
        var fr = Doc("fr", lang: "fr", id: 3, ageDays: 5);
        Assert.Equal("02", Best([en, hi, fr], lang: "hi"));
        Assert.Equal("03", Best([en, hi, fr], lang: "fr", def: "hi"));
        Assert.Equal("02", Best([en, hi, fr], lang: "de", def: "hi"));                          // asked for German: the tenant's default
        Assert.Equal("01", Best([en, hi, fr], lang: "de", def: "ta"));                          // neither: English
        Assert.Equal("01", Best([en, hi, fr]));                                                 // nothing asked: English
        Assert.Equal("02", Best([hi, fr], lang: "de", def: "ta"));                              // no English either: the best-ranked (the newer one)
        Assert.Equal("02", Best([hi, fr.Older(30)], lang: null));
    }

    [Fact]
    public void A_regional_tag_falls_back_to_its_language_and_back()
    {
        var hiIn = Doc("hi-in", lang: "hi-in", id: 1);
        var en = Doc("en", lang: "en", id: 2);
        Assert.Equal("01", Best([hiIn, en], lang: "hi"));            // asked for hi: the hi-in document is the same language
        Assert.Equal("01", Best([hiIn, en], lang: "hi-in"));
        Assert.Equal("01", Best([hiIn, Doc("hi", lang: "hi", id: 3, ageDays: 9)], lang: "hi-in"));
        Assert.Equal("03", Best([hiIn, Doc("hi", lang: "hi", id: 3)], lang: "hi"));              // an exact match is preferred to a regional one
        Assert.Equal("01", Best([hiIn, en], lang: "hi-ur"));          // asked for hi-ur: same primary language
    }

    [Fact]
    public void Other_languages_list_the_best_document_of_each_other_language()
    {
        var sel = DocumentRanking.Select(
            [Doc("en-1", lang: "en", id: 1), Doc("hi-old", lang: "hi", id: 2, ageDays: 9), Doc("hi-new", lang: "hi", id: 3), Doc("ta", lang: "ta", id: 4)],
            "gt", 2024, "en", null)!;
        Assert.Equal("en", sel.Chosen.Language);
        Assert.Equal(["hi", "ta"], sel.OtherLanguages.Select(o => o.Language));
        Assert.Equal(["03", "04"], sel.OtherLanguages.Select(o => o.DocumentId.ToString()[^2..]));
    }

    [Fact]
    public void Documents_for_another_variant_or_year_are_not_candidates_at_all()
    {
        var sel = DocumentRanking.Select([Doc("gt", variant: "gt", id: 1), Doc("xl", variant: "xl", id: 2)], "gt", 2024, null, null)!;
        Assert.Equal("01", sel.Chosen.DocumentId.ToString()[^2..]);
        Assert.Empty(sel.OtherLanguages);
        Assert.Null(DocumentRanking.Select([Doc("xl", variant: "xl")], "gt", 2024, null, null));
        Assert.Null(DocumentRanking.Select([], "gt", 2024, null, null));
    }
}

internal static class CandidateExtensions
{
    public static DocumentCandidate Older(this DocumentCandidate d, int days) => d with { Updated = d.Updated.AddDays(-days) };
}

public sealed class ChapterRulesTests
{
    private static Chapter C(string title, int level, int? from) => new(title, null, level, from, null);

    [Fact]
    public void A_chapter_ends_on_the_page_before_the_next_chapter_of_the_same_or_a_higher_level()
    {
        var chapters = new List<Chapter> { C("One", 1, 1), C("One.a", 2, 1), C("One.b", 2, 3), C("Two", 1, 5), C("Two.a", 2, 7) };
        var r = ChapterRules.WithPageRanges(chapters, 9);
        Assert.Equal([(1, 4), (1, 2), (3, 4), (5, 9), (7, 9)], r.Select(c => (c.PageFrom!.Value, c.PageTo!.Value)));
    }

    [Fact]
    public void The_last_chapter_runs_to_the_last_page_and_a_chapter_is_never_empty()
    {
        var r = ChapterRules.WithPageRanges([C("A", 1, 2), C("B", 1, 2), C("C", 1, 3)], 4);
        Assert.Equal([(2, 2), (2, 2), (3, 4)], r.Select(c => (c.PageFrom!.Value, c.PageTo!.Value)));
    }

    [Fact]
    public void A_chapter_without_a_usable_first_page_has_no_pages_and_does_not_bound_the_others()
    {
        var r = ChapterRules.WithPageRanges([C("A", 1, 1), C("nowhere", 1, null), C("beyond", 1, 99), C("D", 1, 4)], 6);
        Assert.Equal((1, 3), (r[0].PageFrom!.Value, r[0].PageTo!.Value));    // runs up to D, skipping the two that point nowhere
        Assert.Null(r[1].PageFrom);
        Assert.Null(r[1].PageTo);
        Assert.Null(r[2].PageFrom);
        Assert.Equal((4, 6), (r[3].PageFrom!.Value, r[3].PageTo!.Value));
    }

    [Fact]
    public void Pages_that_run_backwards_are_clamped_to_the_chapters_own_first_page()
    {
        var r = ChapterRules.WithPageRanges([C("A", 1, 5), C("B", 1, 2)], 8);
        Assert.Equal((5, 5), (r[0].PageFrom!.Value, r[0].PageTo!.Value));
    }

    [Fact]
    public void Tidy_trims_titles_drops_empty_ones_clamps_levels_and_caps_the_count()
    {
        var tidy = ChapterRules.Tidy([C("  Hello \t  world\n", 0, null), C("   ", 1, null), new Chapter("x", "  top  ", 99, null, null)], out var truncated);
        Assert.False(truncated);
        Assert.Equal(["Hello world", "x"], tidy.Select(c => c.Title));
        Assert.Equal([1, KnowledgeLimits.MaxChapterLevel], tidy.Select(c => c.Level));
        Assert.Equal("top", tidy[1].Anchor);

        var many = ChapterRules.Tidy(Enumerable.Range(0, 600).Select(i => C($"Chapter {i}", 1, null)), out var cut);
        Assert.True(cut);
        Assert.Equal(KnowledgeLimits.MaxChapters, many.Count);
        Assert.Equal("Chapter 499", many[^1].Title);
    }

    [Fact]
    public void A_title_is_limited_to_300_characters_and_stripped_of_control_characters()
    {
        var t = ChapterRules.CleanTitle(new string('é', 400));
        Assert.Equal(KnowledgeLimits.MaxChapterTitle, t.Length);
        Assert.Equal("a b", ChapterRules.CleanTitle("a\u0007b"));
        Assert.Equal("", ChapterRules.CleanTitle(null));
        Assert.Equal("a b", ChapterRules.CleanTitle("a\u0000 b"));
    }
}

public sealed class KnowledgeInputRuleTests
{
    private static readonly TenantLanguages Any = TenantLanguages.Unrestricted;

    [Fact]
    public void A_good_mapping_passes_and_the_language_defaults()
    {
        var e = KnowledgeRules.ValidateMapping("owner_manual", "roadster", "gt", 2024, 2026, null, "ev", Any, out var lang);
        Assert.Empty(e);
        Assert.Equal("en", lang);
        Assert.Empty(KnowledgeRules.ValidateMapping("faq", "roadster", null, null, null, "HI", null, Any, out lang));
        Assert.Equal("hi", lang);
        KnowledgeRules.ValidateMapping("faq", "roadster", null, null, null, null, null, TenantLanguages.Parse("""{"defaultLanguage":"hi"}"""), out lang);
        Assert.Equal("hi", lang);
    }

    [Theory]
    [InlineData("nope", "roadster", null, null, null, "en", null, "docType")]
    [InlineData(null, "roadster", null, null, null, "en", null, "docType")]
    [InlineData("faq", null, null, null, null, "en", null, "model")]
    [InlineData("faq", "Road Ster", null, null, null, "en", null, "model")]
    [InlineData("faq", "roadster", "GT 1", null, null, "en", null, "variant")]
    [InlineData("faq", "roadster", null, null, null, "en", "e v", "theme")]
    [InlineData("faq", "roadster", null, 2024, null, "en", null, "modelYearTo")]
    [InlineData("faq", "roadster", null, null, 2024, "en", null, "modelYearFrom")]
    [InlineData("faq", "roadster", null, 2026, 2024, "en", null, "modelYearTo")]
    [InlineData("faq", "roadster", null, 1900, 2024, "en", null, "modelYearFrom")]
    [InlineData("faq", "roadster", null, 2024, 2200, "en", null, "modelYearTo")]
    [InlineData("faq", "roadster", null, null, null, "e", null, "language")]
    [InlineData("faq", "roadster", null, null, null, "not a language", null, "language")]
    public void Each_bad_field_is_named(string? docType, string? model, string? variant, int? from, int? to, string? language, string? theme, string field)
    {
        var e = KnowledgeRules.ValidateMapping(docType, model, variant, from, to, language, theme, Any, out _);
        Assert.Contains(field, e.Keys);
        Assert.All(e.Values, v => Assert.NotEmpty(v));
    }

    [Fact]
    public void A_language_the_tenant_has_not_enabled_is_refused()
    {
        var langs = TenantLanguages.Parse("""{"languages":["en","hi"]}""");
        Assert.Empty(KnowledgeRules.ValidateMapping("faq", "m", null, null, null, "hi", null, langs, out _));
        var e = KnowledgeRules.ValidateMapping("faq", "m", null, null, null, "ta", null, langs, out _);
        Assert.Contains("not enabled", e["language"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_longer_than_100_characters_is_refused()
    {
        Assert.Contains("model", KnowledgeRules.ValidateMapping("faq", new string('a', 101), null, null, null, "en", null, Any, out _).Keys);
        Assert.Empty(KnowledgeRules.ValidateMapping("faq", new string('a', 100), null, null, null, "en", null, Any, out _));
    }

    private static VinRuleInput Rule(string? name = "r1", string? pattern = "ACM*", string? model = "Roadster", string? variant = null, int? year = null, bool? fromVin = null,
        string? theme = null, int? priority = null, bool? enabled = null) => new(name, pattern, model, variant, year, fromVin, theme, priority, enabled);

    [Fact]
    public void A_rule_is_normalised_and_defaults_are_filled_in()
    {
        var e = KnowledgeRules.ValidateRule(Rule(name: "  Roadster GT  ", pattern: " acm* ", variant: "GT", theme: "EV"), out var facts, out var name);
        Assert.Empty(e);
        Assert.Equal("Roadster GT", name);
        Assert.Equal(new RuleFacts("ACM*", "roadster", "gt", null, true, "ev", 0, true), facts);
    }

    [Fact]
    public void A_fixed_year_turns_off_reading_the_year_from_the_vin()
    {
        KnowledgeRules.ValidateRule(Rule(year: 2022), out var facts, out _);
        Assert.Equal(2022, facts!.ModelYear);
        Assert.False(facts.YearFromVin);
        var both = KnowledgeRules.ValidateRule(Rule(year: 2022, fromVin: true), out _, out _);
        Assert.Contains("yearFromVin", both.Keys);
        KnowledgeRules.ValidateRule(Rule(fromVin: false), out facts, out _);
        Assert.False(facts!.YearFromVin);
        Assert.Null(facts.ModelYear);
    }

    [Theory]
    [InlineData(null, "ACM*", "m", "name")]
    [InlineData("  ", "ACM*", "m", "name")]
    [InlineData("n", null, "m", "pattern")]
    [InlineData("n", "ACM", "m", "pattern")]
    [InlineData("n", "ACM*", null, "model")]
    [InlineData("n", "ACM*", "Bad Model", "model")]
    public void Each_bad_rule_field_is_named(string? name, string? pattern, string? model, string field)
    {
        Assert.Contains(field, KnowledgeRules.ValidateRule(Rule(name: name, pattern: pattern, model: model), out var facts, out _).Keys);
        Assert.Null(facts);
    }

    [Fact]
    public void Name_priority_year_variant_and_theme_have_limits()
    {
        Assert.Contains("name", KnowledgeRules.ValidateRule(Rule(name: new string('n', 201)), out _, out _).Keys);
        Assert.Contains("name", KnowledgeRules.ValidateRule(Rule(name: "a\u0001b"), out _, out _).Keys);
        Assert.Contains("priority", KnowledgeRules.ValidateRule(Rule(priority: 10_001), out _, out _).Keys);
        Assert.Contains("priority", KnowledgeRules.ValidateRule(Rule(priority: -10_001), out _, out _).Keys);
        Assert.Empty(KnowledgeRules.ValidateRule(Rule(priority: 10_000), out _, out _));
        Assert.Contains("modelYear", KnowledgeRules.ValidateRule(Rule(year: 1979), out _, out _).Keys);
        Assert.Contains("modelYear", KnowledgeRules.ValidateRule(Rule(year: 2101), out _, out _).Keys);
        Assert.Contains("variant", KnowledgeRules.ValidateRule(Rule(variant: "no spaces"), out _, out _).Keys);
        Assert.Contains("theme", KnowledgeRules.ValidateRule(Rule(theme: "NO/SLASH"), out _, out _).Keys);
    }

    [Fact]
    public void Entered_chapters_are_checked_and_tidied()
    {
        var ok = KnowledgeRules.ValidateChapters([new ChapterDto("  Intro ", " intro ", 1, 1, 3), new ChapterDto("Next", null, 2, null, null)], out var tidy);
        Assert.Empty(ok);
        Assert.Equal(["Intro", "Next"], tidy.Select(c => c.Title));
        Assert.Equal("intro", tidy[0].Anchor);
        Assert.Empty(KnowledgeRules.ValidateChapters([], out tidy));
        Assert.Empty(tidy);

        Assert.Contains("chapters", KnowledgeRules.ValidateChapters(null, out _).Keys);
        Assert.Contains("chapters", KnowledgeRules.ValidateChapters(Enumerable.Range(0, 501).Select(i => new ChapterDto($"c{i}", null, 1, null, null)).ToList(), out _).Keys);
        Assert.Contains("chapters[0].title", KnowledgeRules.ValidateChapters([new ChapterDto("  ", null, 1, null, null)], out _).Keys);
        Assert.Contains("chapters[0].title", KnowledgeRules.ValidateChapters([new ChapterDto(new string('t', 301), null, 1, null, null)], out _).Keys);
        Assert.Contains("chapters[0].level", KnowledgeRules.ValidateChapters([new ChapterDto("a", null, 0, null, null)], out _).Keys);
        Assert.Contains("chapters[0].level", KnowledgeRules.ValidateChapters([new ChapterDto("a", null, 11, null, null)], out _).Keys);
        Assert.Contains("chapters[0].pageFrom", KnowledgeRules.ValidateChapters([new ChapterDto("a", null, 1, 0, null)], out _).Keys);
        Assert.Contains("chapters[0].pageTo", KnowledgeRules.ValidateChapters([new ChapterDto("a", null, 1, 5, 4)], out _).Keys);
        Assert.Contains("chapters[0].pageTo", KnowledgeRules.ValidateChapters([new ChapterDto("a", null, 1, null, 4)], out _).Keys);
        Assert.Contains("chapters[1].anchor", KnowledgeRules.ValidateChapters([new ChapterDto("a", null, 1, null, null), new ChapterDto("b", new string('x', 201), 1, null, null)], out _).Keys);
        Assert.Empty(KnowledgeRules.ValidateChapters(Enumerable.Range(0, 500).Select(i => new ChapterDto($"c{i}", null, 1, null, null)).ToList(), out _));
    }

    [Fact]
    public void The_knowledge_permission_belongs_to_admin_and_librarian_only_and_ask_assist_is_read_only()
    {
        var with = BuiltInRoles.All.Where(r => r.Permissions.Contains(Permissions.KnowledgeManage)).Select(r => r.Name).Order();
        Assert.Equal([BuiltInRoles.Admin, BuiltInRoles.Librarian], with);
        var assist = BuiltInRoles.Find("ask assist")!;
        Assert.Equal("Ask Assist", assist.Name);
        Assert.Equal(new[] { Permissions.AssetsRead, Permissions.AssetsReadInReview }, assist.Permissions.Order());
        Assert.False(assist.AttributeRestricted);
    }

    [Fact]
    public void Code_hints_and_document_types_are_stable()
    {
        Assert.Equal(["owner_manual", "faq", "service", "training", "product_info"], KnowledgeDocTypes.All);
        Assert.Equal(["manual", "document", "html5"], KnowledgeAssetTypes.Allowed);
        Assert.Null(KnowledgeCodes.Normalize(""));
        Assert.Null(KnowledgeCodes.Normalize(null));
        Assert.Equal("a.b_c-1", KnowledgeCodes.Normalize(" A.B_C-1 "));
    }
}
