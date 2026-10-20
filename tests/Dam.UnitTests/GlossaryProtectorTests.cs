using Dam.Domain.Ai;

namespace Dam.UnitTests;

/// <summary>
/// The guarantee behind "glossary terms are never translated" (ADR-0036): terms are swapped for tokens before the text goes to a provider, and a
/// translation is accepted only if every token came back exactly once and unchanged. Pure, so it is tested hard.
/// </summary>
public sealed class GlossaryProtectorTests
{
    private static GlossaryEntry Locked(string term) => new(term, GlossaryModes.Locked, new Dictionary<string, string>());
    private static GlossaryEntry Mapped(string term, params (string Lang, string Text)[] renderings) =>
        new(term, GlossaryModes.Mapped, renderings.ToDictionary(r => r.Lang, r => r.Text));

    /// <summary>What a faithful but literal translator does to everything outside the tokens.</summary>
    private static string Shout(ProtectedText p) => p.Text.ToUpperInvariant();

    // ---- locked terms ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_locked_term_is_replaced_by_a_token_and_comes_back_byte_for_byte()
    {
        var p = GlossaryProtector.Protect("Meet the Acme Roadster X at the showroom.", [Locked("Acme Roadster X")], "hi");
        Assert.Equal("Meet the ⟦G1⟧ at the showroom.", p.Text);
        Assert.DoesNotContain("Roadster", p.Text);
        var r = GlossaryProtector.Restore(Shout(p), p);
        Assert.True(r.Ok, r.Problem);
        Assert.Equal("MEET THE Acme Roadster X AT THE SHOWROOM.", r.Text);   // the term kept its case: it was never touched
    }

    [Fact]
    public void Nothing_to_protect_means_the_text_is_unchanged_and_any_reply_is_accepted()
    {
        var p = GlossaryProtector.Protect("Plain words.", [Locked("Acme Roadster X")], "hi");
        Assert.False(p.HasTokens);
        Assert.Equal("Plain words.", p.Text);
        var r = GlossaryProtector.Restore("कुछ भी", p);
        Assert.True(r.Ok);
        Assert.Equal("कुछ भी", r.Text);
        Assert.Equal("", GlossaryProtector.Protect("", [Locked("A")], "hi").Text);
        Assert.False(GlossaryProtector.Protect("text", [], "hi").HasTokens);
    }

    [Fact]
    public void The_same_term_twice_gets_two_tokens_that_are_each_restored()
    {
        var p = GlossaryProtector.Protect("Acme Roadster X, then Acme Roadster X again", [Locked("Acme Roadster X")], "ta");
        Assert.Equal("⟦G1⟧, then ⟦G2⟧ again", p.Text);
        var r = GlossaryProtector.Restore(Shout(p), p);
        Assert.Equal("Acme Roadster X, THEN Acme Roadster X AGAIN", r.Text);
    }

    [Fact]
    public void Matching_is_case_sensitive()
    {
        var p = GlossaryProtector.Protect("acme roadster x and ACME ROADSTER X and Acme Roadster X", [Locked("Acme Roadster X")], "hi");
        Assert.Equal("acme roadster x and ACME ROADSTER X and ⟦G1⟧", p.Text);
    }

    // ---- longest first, overlaps -----------------------------------------------------------------------------------

    [Fact]
    public void A_longer_term_claims_its_text_before_a_shorter_one_inside_it()
    {
        var entries = new[] { Locked("Acme"), Locked("Acme Roadster X") };
        var p = GlossaryProtector.Protect("Acme Roadster X from Acme", entries, "hi");
        Assert.Equal("⟦G1⟧ from ⟦G2⟧", p.Text);
        Assert.Equal(["Acme Roadster X", "Acme"], p.Spans.Select(s => s.Original));
        // order of the glossary does not matter
        Assert.Equal(p.Text, GlossaryProtector.Protect("Acme Roadster X from Acme", entries.Reverse().ToArray(), "hi").Text);
    }

    [Fact]
    public void Terms_that_overlap_partly_keep_the_longer_one_and_leave_the_rest_of_the_text_alone()
    {
        var p = GlossaryProtector.Protect("The Acme Roadster X arrives", [Locked("Acme Roadster"), Locked("Roadster X")], "hi");
        Assert.Equal("The ⟦G1⟧ X arrives", p.Text);
        Assert.Equal(["Acme Roadster"], p.Spans.Select(s => s.Original));
    }

    [Fact]
    public void Equal_length_overlaps_are_decided_the_same_way_every_time()
    {
        var a = GlossaryProtector.Protect("Alpha Beta Gamma", [Locked("Alpha Beta"), Locked("Beta Gamma")], "hi");
        var b = GlossaryProtector.Protect("Alpha Beta Gamma", [Locked("Beta Gamma"), Locked("Alpha Beta")], "hi");
        Assert.Equal(a.Text, b.Text);
        Assert.Equal("⟦G1⟧ Gamma", a.Text);        // same length: the one that sorts first wins
        Assert.Single(a.Spans);
    }

    // ---- mapped terms -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_mapped_term_becomes_the_rendering_for_the_target_language()
    {
        var entry = Mapped("Acme Pulse", ("hi", "एक्मे पल्स"), ("ta", "அக்மி பல்ஸ்"));
        var p = GlossaryProtector.Protect("The Acme Pulse is quiet", [entry], "hi");
        var r = GlossaryProtector.Restore(Shout(p), p);
        Assert.Equal("THE एक्मे पल्स IS QUIET", r.Text);
        var ta = GlossaryProtector.Protect("The Acme Pulse is quiet", [entry], "ta");
        Assert.Equal("THE அக்மி பல்ஸ் IS QUIET", GlossaryProtector.Restore(Shout(ta), ta).Text);
    }

    [Fact]
    public void A_mapped_term_with_no_rendering_for_the_language_is_kept_as_written()
    {
        var entry = Mapped("Acme Pulse", ("hi", "एक्मे पल्स"));
        var p = GlossaryProtector.Protect("The Acme Pulse", [entry], "te");
        Assert.Equal("THE Acme Pulse", GlossaryProtector.Restore(Shout(p), p).Text);
    }

    [Fact]
    public void A_regional_code_falls_back_to_its_primary_language_rendering()
    {
        var entry = Mapped("Acme Pulse", ("pt", "Acme Pulso"));
        var p = GlossaryProtector.Protect("The Acme Pulse", [entry], "pt-br");
        Assert.Equal("THE Acme Pulso", GlossaryProtector.Restore(Shout(p), p).Text);
        Assert.Equal("Acme Pulso", entry.RenderIn("PT"));
        Assert.Equal("Acme Pulse", entry.RenderIn("fr"));
    }

    [Fact]
    public void Characters_that_mean_something_to_a_regex_or_a_replacement_come_back_as_they_were()
    {
        var entry = Mapped("Acme $1 (Pro)", ("hi", "एक्मे $1 \\ (प्रो) [x]"));
        var p = GlossaryProtector.Protect("Buy Acme $1 (Pro) now", [entry], "hi");
        Assert.Equal("BUY एक्मे $1 \\ (प्रो) [x] NOW", GlossaryProtector.Restore(Shout(p), p).Text);
    }

    // ---- boundaries -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Acme Xtra is new", "Acme X")]            // the term ends in a letter and a letter follows
    [InlineData("The BAcme X is new", "Acme X")]          // the term starts with a letter and a letter precedes
    [InlineData("Model Acme X2 is new", "Acme X")]        // a digit follows
    [InlineData("Model 3Acme X is new", "Acme X")]        // a digit precedes
    public void An_ascii_term_is_not_taken_from_the_middle_of_a_longer_word(string text, string term)
    {
        var p = GlossaryProtector.Protect(text, [Locked(term)], "hi");
        Assert.False(p.HasTokens);
        Assert.Equal(text, p.Text);
    }

    [Theory]
    [InlineData("(Acme X) is new", "(⟦G1⟧) is new")]
    [InlineData("Acme X, Acme X; and Acme X.", "⟦G1⟧, ⟦G2⟧; and ⟦G3⟧.")]
    [InlineData("\"Acme X\"", "\"⟦G1⟧\"")]
    [InlineData("Acme X's range", "⟦G1⟧'s range")]
    [InlineData("Acme X-based", "⟦G1⟧-based")]
    [InlineData("Acme X\nAcme X", "⟦G1⟧\n⟦G2⟧")]
    [InlineData("«Acme X»", "«⟦G1⟧»")]
    public void A_term_next_to_punctuation_or_a_line_break_is_found(string text, string expected)
    {
        Assert.Equal(expected, GlossaryProtector.Protect(text, [Locked("Acme X")], "hi").Text);
    }

    [Fact]
    public void A_term_that_itself_ends_in_punctuation_needs_no_boundary_there()
    {
        var p = GlossaryProtector.Protect("Try Acme X!now", [Locked("Acme X!")], "hi");
        Assert.Equal("Try ⟦G1⟧now", p.Text);
    }

    // ---- Indic scripts ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("एक्मे रोडस्टर", "hi")]
    [InlineData("அக்மி ரோட்ஸ்டர்", "ta")]
    [InlineData("అక్మే రోడ్‌స్టర్", "te")]
    public void An_indic_term_is_matched_wherever_it_appears_because_these_scripts_have_no_reliable_word_boundary(string term, string language)
    {
        var text = $"नया{term}आया और {term}।";
        var p = GlossaryProtector.Protect(text, [Locked(term)], language);
        Assert.Equal("नया⟦G1⟧आया और ⟦G2⟧।", p.Text);
        var r = GlossaryProtector.Restore(p.Text, p);
        Assert.True(r.Ok);
        Assert.Equal(text, r.Text);   // a translator that changes nothing gives the text back exactly
    }

    [Fact]
    public void A_mixed_script_term_is_checked_for_a_boundary_only_at_its_ascii_end()
    {
        var term = "Acme रोडस्टर";
        Assert.Equal("⟦G1⟧ है", GlossaryProtector.Protect("Acme रोडस्टर है", [Locked(term)], "hi").Text);
        Assert.Equal("⟦G1⟧है", GlossaryProtector.Protect("Acme रोडस्टरहै", [Locked(term)], "hi").Text);          // nothing is required after the Devanagari end
        Assert.False(GlossaryProtector.Protect("BAcme रोडस्टर", [Locked(term)], "hi").HasTokens);                  // a letter before the ASCII start blocks it
    }

    // ---- verification -----------------------------------------------------------------------------------------------

    private static ProtectedText Two() => GlossaryProtector.Protect("Acme Roadster X and Acme Pulse", [Locked("Acme Roadster X"), Locked("Acme Pulse")], "hi");

    [Fact]
    public void A_lost_token_fails_the_translation()
    {
        var r = GlossaryProtector.Restore("और ⟦G2⟧", Two());
        Assert.False(r.Ok);
        Assert.Contains("Acme Roadster X", r.Problem);
    }

    [Fact]
    public void A_repeated_token_fails_the_translation()
    {
        var r = GlossaryProtector.Restore("⟦G1⟧ और ⟦G2⟧ और ⟦G1⟧", Two());
        Assert.False(r.Ok);
        Assert.Contains("repeated", r.Problem);
    }

    [Theory]
    [InlineData("⟦ G1 ⟧ और ⟦G2⟧")]       // spaces added
    [InlineData("⟦g1⟧ और ⟦G2⟧")]         // case changed
    [InlineData("[[G1]] और ⟦G2⟧")]       // other brackets
    [InlineData("⟦G1 और ⟦G2⟧")]          // a closing bracket lost
    [InlineData("⟦G१⟧ और ⟦G2⟧")]         // the digit written in Devanagari
    [InlineData("<G1> और ⟦G2⟧")]
    [InlineData("G1 और ⟦G2⟧")]           // brackets dropped
    [InlineData("⟦ज1⟧ और ⟦G2⟧")]         // the letter translated
    public void An_altered_token_counts_as_lost_and_fails_the_translation(string reply)
    {
        var r = GlossaryProtector.Restore(reply, Two());
        Assert.False(r.Ok);
    }

    [Fact]
    public void A_token_that_was_never_sent_fails_the_translation()
    {
        var r = GlossaryProtector.Restore("⟦G1⟧ और ⟦G2⟧ और ⟦G3⟧", Two());
        Assert.False(r.Ok);
        Assert.Contains("never sent", r.Problem);
    }

    [Fact]
    public void Swapped_tokens_are_accepted_because_word_order_changes_between_languages()
    {
        var r = GlossaryProtector.Restore("⟦G2⟧ और ⟦G1⟧", Two());
        Assert.True(r.Ok);
        Assert.Equal("Acme Pulse और Acme Roadster X", r.Text);
    }

    [Fact]
    public void A_failed_restore_returns_the_reply_as_received_so_nothing_half_done_can_leak()
    {
        var r = GlossaryProtector.Restore("only ⟦G1⟧", Two());
        Assert.False(r.Ok);
        Assert.Equal("only ⟦G1⟧", r.Text);
    }

    // ---- the text already contains marker-like characters ---------------------------------------------------------------

    [Fact]
    public void Brackets_that_were_in_the_source_text_never_collide_with_the_tokens()
    {
        var p = GlossaryProtector.Protect("Legacy ⟦G1⟧ marker for Acme Pulse", [Locked("Acme Pulse")], "hi");
        Assert.Equal("GG", p.Prefix);
        Assert.Equal("Legacy ⟦G1⟧ marker for ⟦GG1⟧", p.Text);
        var r = GlossaryProtector.Restore(Shout(p), p);
        Assert.True(r.Ok, r.Problem);
        Assert.Equal("LEGACY ⟦G1⟧ MARKER FOR Acme Pulse", r.Text);
    }

    [Fact]
    public void The_instruction_for_a_provider_names_the_token_shape_and_is_empty_when_there_are_none()
    {
        var instruction = GlossaryProtector.Instruction(Two());
        Assert.Contains("⟦G1⟧", instruction);
        Assert.Contains("exactly as written", instruction);
        Assert.Equal("", GlossaryProtector.Instruction(GlossaryProtector.Protect("nothing", [], "hi")));
    }

    [Fact]
    public void Protecting_is_deterministic()
    {
        var entries = new[] { Locked("Acme"), Locked("Acme Roadster X"), Mapped("Pulse", ("hi", "पल्स")) };
        const string text = "Acme Roadster X and the Pulse by Acme.";
        var a = GlossaryProtector.Protect(text, entries, "hi");
        var b = GlossaryProtector.Protect(text, entries, "hi");
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Spans, b.Spans);
    }

    [Fact]
    public void A_long_text_with_a_big_glossary_is_protected_quickly()
    {
        var entries = Enumerable.Range(0, 5000).Select(i => Locked($"Acme Model {i:D4}")).ToList();
        var text = string.Concat(Enumerable.Repeat("The Acme Model 0042 and plain words go on. ", 400));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var p = GlossaryProtector.Protect(text, entries, "hi");
        watch.Stop();
        Assert.Equal(400, p.Spans.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
