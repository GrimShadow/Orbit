using Dam.Domain.Ai;

namespace Dam.UnitTests;

/// <summary>The built-in summariser of the self-hosted provider (ADR-0036): sentences chosen from the text itself, any script, no model.</summary>
public sealed class ExtractiveSummaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("... !!! ???")]
    [InlineData("।।॥")]
    public void Nothing_to_summarise_gives_an_empty_summary(string? text) => Assert.Equal("", ExtractiveSummary.Summarise(text));

    [Fact]
    public void Very_short_text_is_returned_as_it_is()
    {
        Assert.Equal("Acme roadster", ExtractiveSummary.Summarise("  Acme roadster  "));
        Assert.Equal("One sentence only.", ExtractiveSummary.Summarise("One sentence only."));
        Assert.Equal("Opens at nine. Closes at six.", ExtractiveSummary.Summarise("Opens at nine.\nCloses at six."));
    }

    [Fact]
    public void Text_that_fits_the_budget_is_kept_whole_with_repeated_sentences_said_once()
    {
        // a running header repeated on every page of a scan
        var pages = string.Join("\n\n", Enumerable.Repeat("Acme showroom guide. Opens at nine.", 3));
        Assert.Equal("Acme showroom guide. Opens at nine.", ExtractiveSummary.Summarise(pages, 120));
        Assert.Equal("Acme showroom guide. Opens at nine.", ExtractiveSummary.Summarise("acme showroom guide. Opens at nine.\nACME SHOWROOM GUIDE. Opens at nine.", 120).Replace("acme", "Acme", StringComparison.Ordinal));
    }

    private static string LongText() => string.Join(' ',
        "The Acme roadster is a two seat convertible built for the coast.",
        "Gardening tips for the spring include watering early in the morning.",
        "The roadster engine is tuned for smooth power and the roadster chassis is light.",
        "A recipe for lentil soup needs onions, garlic and patience.",
        "Every Acme roadster comes with a panoramic roof and a five year warranty.",
        "The weather last week was unusually mild for the season.",
        "Owners praise the roadster handling on winding coastal roads.",
        "A short note about parking rules in the old town centre.",
        "The roadster is available in four colours and two trims.");

    [Fact]
    public void A_long_text_is_cut_to_the_budget_keeping_the_sentences_that_carry_its_subject_in_their_original_order()
    {
        var text = LongText();
        var summary = ExtractiveSummary.Summarise(text, 30);

        Assert.True(ExtractiveSummary.WordCount(summary) <= 30, summary);
        Assert.True(ExtractiveSummary.WordCount(summary) >= 15, summary);
        Assert.Contains("roadster", summary);
        Assert.DoesNotContain("lentil", summary);
        Assert.DoesNotContain("parking", summary);
        // every sentence in the summary is a whole sentence of the text, and they appear in the text's order
        var chosen = ExtractiveSummary.Sentences(summary).ToList();
        var all = ExtractiveSummary.Sentences(text).ToList();
        Assert.All(chosen, c => Assert.Contains(c, all));
        Assert.Equal(chosen.Select(c => all.IndexOf(c)).Order(), chosen.Select(c => all.IndexOf(c)));
    }

    [Fact]
    public void The_same_text_always_gives_the_same_summary_and_a_bigger_budget_gives_a_longer_one()
    {
        var text = LongText();
        Assert.Equal(ExtractiveSummary.Summarise(text, 40), ExtractiveSummary.Summarise(text, 40));
        Assert.True(ExtractiveSummary.WordCount(ExtractiveSummary.Summarise(text, 60)) > ExtractiveSummary.WordCount(ExtractiveSummary.Summarise(text, 20)));
        Assert.Equal(ExtractiveSummary.Summarise(text, 100000), ExtractiveSummary.Summarise(text, 2000));
    }

    [Fact]
    public void A_single_sentence_longer_than_the_budget_is_cut_at_a_word()
    {
        var one = "This single sentence goes on and on without any stop so a summary of it has to be cut somewhere sensible";
        var summary = ExtractiveSummary.Summarise(one, 5);
        Assert.Equal("This single sentence goes on…", summary);
    }

    [Fact]
    public void The_hindi_danda_ends_a_sentence_and_combining_marks_stay_inside_their_word()
    {
        var text = "एक्मे रोडस्टर एक नई गाड़ी है। यह तटीय सड़कों के लिए बनी है। मौसम आज बहुत अच्छा है। रोडस्टर में पैनोरमिक छत है। दुकान सुबह नौ बजे खुलती है॥";
        var sentences = ExtractiveSummary.Sentences(text).ToList();
        Assert.Equal(5, sentences.Count);
        Assert.EndsWith("।", sentences[0]);
        Assert.EndsWith("॥", sentences[4]);
        Assert.Equal(["एक्मे", "रोडस्टर", "एक", "नई", "गाड़ी", "है"], ExtractiveSummary.Words(sentences[0]));

        var summary = ExtractiveSummary.Summarise(text, 14);
        Assert.True(ExtractiveSummary.WordCount(summary) <= 14, summary);
        Assert.Contains("रोडस्टर", summary);
        Assert.All(ExtractiveSummary.Sentences(summary), s => Assert.True(s.EndsWith('।') || s.EndsWith('॥'), s));     // whole sentences, ended by their danda
        Assert.Equal(text, ExtractiveSummary.Summarise(text, 120));                                                      // fits: kept whole
    }

    [Fact]
    public void Tamil_and_telugu_text_is_summarised_too()
    {
        var tamil = "அக்மி ரோட்ஸ்டர் ஒரு புதிய வாகனம். இது கடற்கரை சாலைகளுக்காக உருவாக்கப்பட்டது. ரோட்ஸ்டர் மென்மையான சக்தி கொண்டது. இன்று வானிலை நன்றாக உள்ளது.";
        var summary = ExtractiveSummary.Summarise(tamil, 12);
        Assert.True(ExtractiveSummary.WordCount(summary) <= 12);
        Assert.NotEqual("", summary);
        var telugu = "అక్మే రోడ్‌స్టర్ ఒక కొత్త వాహనం. ఇది తీర రోడ్ల కోసం తయారు చేయబడింది.";
        Assert.Equal(telugu, ExtractiveSummary.Summarise(telugu, 100));
    }

    [Fact]
    public void Decimal_points_and_abbreviations_without_a_following_space_do_not_end_a_sentence()
    {
        var sentences = ExtractiveSummary.Sentences("The engine makes 3.5 litres of torque. See www.acme.example for more. Done!").ToList();
        Assert.Equal(3, sentences.Count);
        Assert.Equal("The engine makes 3.5 litres of torque.", sentences[0]);
    }

    [Fact]
    public void Several_marks_in_a_row_belong_to_one_sentence_and_blank_lines_are_ignored()
    {
        Assert.Equal(["Really?!", "Yes...", "it is."], ExtractiveSummary.Sentences("Really?!  Yes... it is.\n\n\n"));
        Assert.Equal(["first line", "second line"], ExtractiveSummary.Sentences("first line\r\n\r\nsecond line"));
    }

    [Fact]
    public void Words_are_runs_of_letters_digits_and_marks_in_lower_case()
    {
        Assert.Equal(["acme", "x2", "है"], ExtractiveSummary.Words("Acme, X2! है।"));
        Assert.Equal(0, ExtractiveSummary.WordCount("--- ... ।"));
    }

    [Fact]
    public void Stop_words_do_not_carry_a_sentence()
    {
        // "the" is everywhere; the two sentences that share the real word "turbine" outrank the one full of stop words
        var text = "The turbine is the heart of the plant. The turbine hall holds the rotor and the stator. The and the and the and the of the of the of the.";
        var summary = ExtractiveSummary.Summarise(text, 17);
        Assert.Contains("turbine", summary);
        Assert.DoesNotContain("of the of the", summary);
    }
}
