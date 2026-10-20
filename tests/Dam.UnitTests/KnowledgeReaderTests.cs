using System.IO.Compression;
using System.Text;
using Dam.Application.Knowledge;
using Dam.Domain.Knowledge;
using Dam.Infrastructure.Knowledge;
using Dam.UnitTests.Support;
using static Dam.UnitTests.Support.TestImages;

namespace Dam.UnitTests;

/// <summary>Chapter indexing (ADR-0039 decision 7): the PDF outline, HTML headings and the entry page of an HTML5 bundle, read from files built in the tests.</summary>
public sealed class PdfOutlineReaderTests
{
    private static readonly OutlineEntry[] Manual =
    [
        new("Welcome", 1, 1), new("Safety", 1, 2), new("Seat belts", 2, 3), new("Airbags", 2, 5), new("Driving", 1, 7), new("Starting", 2, 7), new("Index", 1, 10),
    ];

    [Fact]
    public void Bookmarks_become_chapters_with_levels_and_derived_page_ranges()
    {
        var r = PdfOutlineReader.Read(PdfWithOutline(10, Manual));
        Assert.Equal(ChapterSources.Pdf, r.Source);
        Assert.False(r.Truncated);
        Assert.Equal(10, r.PageCount);
        Assert.Equal(
            [("Welcome", 1, 1, 1), ("Safety", 1, 2, 6), ("Seat belts", 2, 3, 4), ("Airbags", 2, 5, 6), ("Driving", 1, 7, 9), ("Starting", 2, 7, 9), ("Index", 1, 10, 10)],
            r.Chapters.Select(c => (c.Title, c.Level, c.PageFrom, c.PageTo)));
        Assert.All(r.Chapters, c => Assert.Null(c.Anchor));
    }

    [Fact]
    public void A_pdf_without_an_outline_has_no_chapters_but_its_page_count_is_known()
    {
        var r = PdfOutlineReader.Read(Pdf("Plain", pages: 4));
        Assert.Equal(ChapterSources.None, r.Source);
        Assert.Empty(r.Chapters);
        Assert.Equal(4, r.PageCount);
    }

    [Fact]
    public void A_file_that_is_not_a_pdf_or_is_damaged_gives_nothing_and_does_not_throw()
    {
        Assert.Empty(PdfOutlineReader.Read([]).Chapters);
        Assert.Empty(PdfOutlineReader.Read("this is not a pdf at all"u8.ToArray()).Chapters);
        var good = PdfWithOutline(10, Manual);
        Assert.Equal(ChapterSources.None, PdfOutlineReader.Read(good[..(good.Length / 2)]).Source);       // cut in half
        var corrupt = (byte[])good.Clone();
        for (var i = 20; i < corrupt.Length; i += 7) corrupt[i] = (byte)'#';
        var ex = Record.Exception(() => PdfOutlineReader.Read(corrupt));
        Assert.Null(ex);
    }

    [Fact]
    public void A_bookmark_that_points_nowhere_takes_its_first_childs_page_and_one_with_no_page_at_all_has_none()
    {
        var r = PdfOutlineReader.Read(PdfWithOutline(8,
        [
            new("Part A", 1, null), new("A1", 2, 2), new("A2", 2, 4), new("Part B", 1, null), new("Appendix", 1, 8),
        ]));
        Assert.Equal(ChapterSources.Pdf, r.Source);
        var by = r.Chapters.ToDictionary(c => c.Title);
        Assert.Equal(2, by["Part A"].PageFrom);          // from A1
        Assert.Equal(7, by["Part A"].PageTo);            // up to the page before the next level-1 chapter that has a page (Appendix)
        Assert.Null(by["Part B"].PageFrom);              // no children, no page
        Assert.Null(by["Part B"].PageTo);
        Assert.Equal((2, 3), (by["A1"].PageFrom!.Value, by["A1"].PageTo!.Value));
        Assert.Equal((4, 7), (by["A2"].PageFrom!.Value, by["A2"].PageTo!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_named_destination_is_looked_up_and_one_that_cannot_be_resolved_gives_a_chapter_without_pages(bool legacyDests)
    {
        var r = PdfOutlineReader.Read(PdfWithOutline(6,
        [
            new("Gone", 1, null, Named: true), new("By name", 1, 2, Named: true), new("Far away", 1, 99), new("Real", 1, 4),
        ], legacyDests: legacyDests));
        Assert.Equal(ChapterSources.Pdf, r.Source);
        var by = r.Chapters.ToDictionary(c => c.Title);
        Assert.Null(by["Gone"].PageFrom);                                        // the name resolves to nothing
        Assert.Equal((2, 3), (by["By name"].PageFrom!.Value, by["By name"].PageTo!.Value));
        Assert.Null(by["Far away"].PageFrom);                                    // the destination is a page that is not in the file
        Assert.Equal((4, 6), (by["Real"].PageFrom!.Value, by["Real"].PageTo!.Value));
    }

    [Fact]
    public void An_outline_that_loops_back_on_itself_ends()
    {
        var r = PdfOutlineReader.Read(PdfWithOutline(6, [new("One", 1, 1), new("Two", 1, 3), new("Three", 1, 5)], loop: true));
        Assert.True(r.Chapters.Count is >= 3 and <= KnowledgeLimits.MaxChapters, $"{r.Chapters.Count} chapters");
        Assert.Equal(["One", "Two", "Three"], r.Chapters.Select(c => c.Title).Take(3));
        Assert.Equal(r.Chapters.Count, r.Chapters.Select(c => c.Title).Count());
    }

    [Fact]
    public void A_very_large_outline_is_capped_at_500_chapters()
    {
        var entries = Enumerable.Range(1, 650).Select(i => new OutlineEntry($"Section {i}", 1, 1 + i % 20)).ToList();
        var r = PdfOutlineReader.Read(PdfWithOutline(20, entries));
        Assert.Equal(KnowledgeLimits.MaxChapters, r.Chapters.Count);
        Assert.True(r.Truncated);
        Assert.Equal("Section 1", r.Chapters[0].Title);
        Assert.Equal("Section 500", r.Chapters[^1].Title);
    }

    [Fact]
    public void A_deeply_nested_outline_is_read_without_overflowing_the_stack_and_levels_are_capped()
    {
        var entries = Enumerable.Range(1, 120).Select(i => new OutlineEntry($"Level {i}", i, 1 + i % 5)).ToList();
        var r = PdfOutlineReader.Read(PdfWithOutline(5, entries));
        Assert.NotEmpty(r.Chapters);
        Assert.All(r.Chapters, c => Assert.InRange(c.Level, 1, KnowledgeLimits.MaxChapterLevel));
    }

    [Fact]
    public void Titles_are_cleaned_and_empty_ones_dropped()
    {
        var r = PdfOutlineReader.Read(PdfWithOutline(3, [new("  Tyres   and  wheels ", 1, 1), new("   ", 1, 2), new("(Odd) title", 1, 3)]));
        Assert.Equal(["Tyres and wheels", "(Odd) title"], r.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Reading_the_same_file_twice_gives_the_same_answer()
    {
        var pdf = PdfWithOutline(10, Manual);
        Assert.Equal(PdfOutlineReader.Read(pdf).Chapters, PdfOutlineReader.Read(pdf).Chapters);
    }
}

public sealed class HtmlHeadingReaderTests
{
    private static List<(string Title, int Level, string? Anchor)> Read(string html) =>
        HtmlHeadingReader.Read(html).Chapters.Select(c => (c.Title, c.Level, c.Anchor)).ToList();

    [Fact]
    public void Headings_one_to_three_become_chapters_with_their_ids_as_anchors()
    {
        var r = HtmlHeadingReader.Read("""
            <html><head><title>Not a heading</title></head><body>
            <h1 id="intro">Introduction</h1><p>text</p>
            <h2 id='safety'>Safety</h2>
            <H3 ID=belts>Seat belts</H3>
            <h4 id="deep">Too deep</h4>
            <h2>No id here</h2>
            </body></html>
            """);
        Assert.Equal(ChapterSources.Html, r.Source);
        Assert.Equal(
            [("Introduction", 1, "intro"), ("Safety", 2, "safety"), ("Seat belts", 3, "belts"), ("No id here", 2, "no-id-here")],
            r.Chapters.Select(c => (c.Title, c.Level, c.Anchor)));
        Assert.All(r.Chapters, c => { Assert.Null(c.PageFrom); Assert.Null(c.PageTo); });
    }

    [Fact]
    public void Generated_slugs_are_unique_and_never_collide_with_an_id_on_the_page()
    {
        var r = Read("<h1>Setup</h1><h2>Setup</h2><h2 id=\"setup-3\">Other</h2><h2>Setup</h2><h2>  </h2><h2>!!!</h2>");
        Assert.Equal(["setup", "setup-2", "setup-3", "setup-4", "section"], r.Select(x => x.Anchor));
    }

    [Fact]
    public void Nested_markup_entities_comments_and_scripts_are_handled()
    {
        var r = Read("""
            <!-- <h1>commented out</h1> -->
            <script>var s = "<h1>in a script</h1>";</script>
            <style>h1 { content: "<h1>x</h1>" }</style>
            <h1>Fuel &amp; <em>range</em> &mdash; <span class="x">tips</span><script>evil()</script></h1>
            <h2>Caf&eacute;   au lait</h2>
            <h2><a href="#x">Linked</a> heading</h2>
            """);
        Assert.Equal(["Fuel & range \u2014 tips", "Caf\u00e9 au lait", "Linked heading"], r.Select(x => x.Title));
    }

    [Fact]
    public void A_quote_or_angle_bracket_inside_an_attribute_does_not_confuse_the_scan()
    {
        var r = Read("<h1 data-x=\"a > b\" id=\"one\">First</h1><h2 title='it''s' id=two>Second</h2>");
        Assert.Equal("First", r[0].Title);
        Assert.Equal("one", r[0].Anchor);
        Assert.Contains(r, x => x.Title == "Second");
    }

    [Fact]
    public void An_unclosed_heading_ends_where_the_next_heading_starts_and_a_stray_less_than_is_just_text()
    {
        var r = Read("<h1>Open<h2>Second</h2><h2>1 < 2 is true</h2>");
        Assert.Equal(["Open", "Second", "1 < 2 is true"], r.Select(x => x.Title));
    }

    [Fact]
    public void Headings_with_no_text_and_pages_with_no_headings_give_nothing()
    {
        Assert.Empty(Read("<h1></h1><h2> </h2><p>just text</p>"));
        Assert.Equal(ChapterSources.None, HtmlHeadingReader.Read("<p>no headings</p>").Source);
        Assert.Empty(Read(""));
    }

    [Fact]
    public void At_most_500_headings_are_kept()
    {
        var html = string.Concat(Enumerable.Range(1, 600).Select(i => $"<h2>Heading {i}</h2>"));
        var r = HtmlHeadingReader.Read(html);
        Assert.Equal(KnowledgeLimits.MaxChapters, r.Chapters.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void A_huge_page_is_read_only_up_to_the_limit_and_nothing_is_fetched()
    {
        var html = "<h1>First</h1>" + new string('x', HtmlHeadingReader.MaxHtmlChars) + "<h1>Beyond the limit</h1><img src=\"http://example.invalid/x.png\">";
        var r = Read(html);
        Assert.Equal(["First"], r.Select(x => x.Title));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        foreach (var junk in new[] { "<", "<<<<", "<h1", "<h1 id=", "<h1 id=\"", "</h1>", "<!--", "<h1><!--", "<script", "<h1>a</h1", "\0\0\0", "<h1 =>x</h1>" })
            Assert.Null(Record.Exception(() => HtmlHeadingReader.Read(junk)));
    }

    [Fact]
    public void The_attribute_reader_handles_the_ways_an_id_is_written()
    {
        Assert.Equal("a", HtmlHeadingReader.AttributeValue("<h1 id=\"a\">", "id"));
        Assert.Equal("a", HtmlHeadingReader.AttributeValue("<h1 id='a'>", "id"));
        Assert.Equal("a", HtmlHeadingReader.AttributeValue("<h1 id=a>", "id"));
        Assert.Equal("a", HtmlHeadingReader.AttributeValue("<h1 class=\"x y\" ID = \"a\" >", "id"));
        Assert.Null(HtmlHeadingReader.AttributeValue("<h1 class=\"x\">", "id"));
        Assert.Null(HtmlHeadingReader.AttributeValue("<h1 id=\"\">", "id"));
        Assert.Null(HtmlHeadingReader.AttributeValue("<h1 data-id=\"a\">", "id"));
        Assert.Null(HtmlHeadingReader.AttributeValue("<h1>", "id"));
    }
}

public sealed class BundleEntryReaderTests
{
    private static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                var e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    private static (string? Html, string? Entry) Read(params (string Name, string Content)[] files)
    {
        using var zip = Zip(files);
        return BundleEntryReader.ReadEntryPage(zip);
    }

    [Fact]
    public void The_entry_page_is_index_html_at_the_top_of_the_bundle()
    {
        var (html, entry) = Read(("assets/app.js", "x"), ("index.html", "<h1>Hi</h1>"), ("other.html", "<h1>Other</h1>"));
        Assert.Equal("index.html", entry);
        Assert.Equal("<h1>Hi</h1>", html);
    }

    [Theory]
    [InlineData("manual/index.html", "manual/index.html")]
    [InlineData("a/b/index.htm", "a/b/index.htm")]
    public void An_index_inside_the_only_folder_is_found(string path, string expected)
    {
        Assert.Equal(expected, Read((path, "<h1>x</h1>"), ("manual/style.css", "")).Entry);
    }

    [Fact]
    public void The_shallowest_index_wins_and_a_single_html_file_is_enough_when_there_is_no_index()
    {
        Assert.Equal("index.html", Read(("deep/er/index.html", ""), ("index.html", ""), ("x/index.html", "")).Entry);
        Assert.Equal("x/index.html", Read(("deep/er/index.html", ""), ("x/index.html", "")).Entry);
        Assert.Equal("guide.html", Read(("guide.html", "<h1>G</h1>"), ("css/a.css", "")).Entry);
        Assert.Null(Read(("a.html", ""), ("b.html", "")).Html);                      // two candidates and no index: not guessed
        Assert.Null(Read(("readme.txt", "x")).Html);                                 // no HTML at all
    }

    [Fact]
    public void Entries_that_could_escape_the_bundle_are_never_candidates()
    {
        Assert.Null(Read(("../evil/index.html", "<h1>Evil</h1>")).Html);
        Assert.Null(Read(("a/../../index.html", "<h1>Evil</h1>")).Html);
        Assert.Null(Read(("/etc/index.html", "<h1>Evil</h1>")).Html);
        Assert.Null(Read(("C:/index.html", "<h1>Evil</h1>")).Html);
        Assert.Null(Read(("..\\index.html", "<h1>Evil</h1>")).Html);
        Assert.Null(Read(("a\\b\\index.html", "<h1>Evil</h1>")).Html);
        Assert.Equal("index.html", Read(("../evil/index.html", "<h1>Evil</h1>"), ("index.html", "<h1>Fine</h1>")).Entry);       // the safe one is used
        Assert.Equal("sub/ok.html", Read(("sub/ok.html", "<h1>Fine</h1>"), ("../x.html", "")).Entry);
    }

    [Fact]
    public void Safe_path_accepts_ordinary_names_and_refuses_the_rest()
    {
        Assert.Equal("a/b.html", BundleEntryReader.SafePath("a/b.html"));
        Assert.Equal("a/", BundleEntryReader.SafePath("a/"));
        foreach (var bad in new[] { "", "../x", "a/../x", "/abs", "c:/x", "a\\b", "a\u0000b", new string('a', 600) })
            Assert.Null(BundleEntryReader.SafePath(bad));
    }

    [Fact]
    public void A_page_larger_than_the_limit_is_not_read()
    {
        var big = "<h1>x</h1>" + new string('y', BundleEntryReader.MaxPageBytes);
        Assert.Null(Read(("index.html", big)).Html);
    }

    [Fact]
    public void An_archive_with_too_many_entries_is_not_trusted()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < BundleEntryReader.MaxEntries + 1; i++) z.CreateEntry($"f/{i}.txt");
            var e = z.CreateEntry("index.html");
            using var w = new StreamWriter(e.Open());
            w.Write("<h1>Hi</h1>");
        }
        ms.Position = 0;
        Assert.Null(BundleEntryReader.ReadEntryPage(ms).Html);
    }

    [Fact]
    public void Things_that_are_not_a_zip_give_nothing_and_do_not_throw()
    {
        Assert.Null(BundleEntryReader.ReadEntryPage(new MemoryStream("not a zip"u8.ToArray())).Html);
        Assert.Null(BundleEntryReader.ReadEntryPage(new MemoryStream([])).Html);
        using var good = Zip(("index.html", "<h1>x</h1>"));
        var bytes = good.ToArray();
        Assert.Null(BundleEntryReader.ReadEntryPage(new MemoryStream(bytes[..(bytes.Length / 2)])).Html);
    }

    [Fact]
    public void Text_is_decoded_from_utf8_with_or_without_a_byte_order_mark_and_utf16_and_latin1()
    {
        Assert.Equal("caf\u00e9", BundleEntryReader.DecodeText(Encoding.UTF8.GetBytes("caf\u00e9")));
        Assert.Equal("caf\u00e9", BundleEntryReader.DecodeText([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("caf\u00e9")]));
        Assert.Equal("caf\u00e9", BundleEntryReader.DecodeText([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("caf\u00e9")]));
        Assert.Equal("caf\u00e9", BundleEntryReader.DecodeText([.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("caf\u00e9")]));
        Assert.Equal("caf\u00e9", BundleEntryReader.DecodeText([(byte)'c', (byte)'a', (byte)'f', 0xE9]));      // not valid UTF-8: Latin-1
    }

    [Fact]
    public void The_entry_page_feeds_the_heading_reader()
    {
        var (html, _) = Read(("index.html", "<html><body><h1 id=\"top\">Owner's manual</h1><h2>Charging</h2></body></html>"));
        var r = HtmlHeadingReader.Read(html!);
        Assert.Equal([("Owner's manual", "top"), ("Charging", "charging")], r.Chapters.Select(c => (c.Title, c.Anchor)));
    }
}

public sealed class CsvTextTests
{
    private static List<string[]> Cells(string csv) => CsvText.Parse(csv).Select(r => r.Cells.ToArray()).ToList();

    [Fact]
    public void Plain_values_are_split_and_trimmed()
    {
        Assert.Equal([["a", "b", "c"], ["1", "2", "3"]], Cells("a, b ,c\n1,2,3\n"));
    }

    [Fact]
    public void Quoted_values_keep_commas_quotes_line_breaks_and_spaces()
    {
        Assert.Equal([["x,y", "he said \"hi\"", "two\nlines", "  padded  "]], Cells("\"x,y\",\"he said \"\"hi\"\"\",\"two\nlines\",\"  padded  \""));
    }

    [Fact]
    public void Windows_line_endings_a_byte_order_mark_and_blank_lines_are_handled()
    {
        var r = CsvText.Parse("\uFEFFname,pattern\r\n\r\nr1,ACM*\r\n   \r\nr2,XYZ*\r\n");
        Assert.Equal(3, r.Count);
        Assert.Equal(["name", "pattern"], r[0].Cells);
        Assert.Equal(1, r[0].Line);
        Assert.Equal(3, r[1].Line);
        Assert.Equal(5, r[2].Line);
    }

    [Fact]
    public void A_record_that_follows_a_multi_line_value_reports_the_right_line()
    {
        var r = CsvText.Parse("a,\"x\ny\"\nb,c");
        Assert.Equal([1, 3], r.Select(x => x.Line));
    }

    [Fact]
    public void Empty_cells_stay_empty_and_a_missing_last_newline_is_fine()
    {
        Assert.Equal([["a", "", "c"], ["", "", ""]], Cells("a,,c\n,,"));
    }

    [Fact]
    public void A_quote_that_is_never_closed_is_an_error_naming_the_line()
    {
        var ex = Assert.Throws<FormatException>(() => CsvText.Parse("a,b\nc,\"oops"));
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_in_the_middle_of_an_unquoted_value_is_just_a_character()
    {
        Assert.Equal([["5\" wheel", "ok"]], Cells("5\" wheel,ok"));
    }
}

public sealed class VinRuleCsvTests
{
    [Fact]
    public void Without_a_header_the_columns_are_in_the_documented_order()
    {
        var lines = VinRuleCsv.Read("ACME GT,ACMRS*,roadster,gt,vin,ev,5,true\nplain,ACMX*,roadster,,,,,\n");
        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0].Error);
        Assert.Equal(new RuleFacts("ACMRS*", "roadster", "gt", null, true, "ev", 5, true), lines[0].Facts);
        Assert.Equal(new RuleFacts("ACMX*", "roadster", null, null, true, null, 0, true), lines[1].Facts);
        Assert.Equal(1, lines[0].Number);
    }

    [Fact]
    public void A_header_may_reorder_columns_and_leave_optional_ones_out()
    {
        var lines = VinRuleCsv.Read("Model,Name,Pattern,Priority\nroadster,r1,ACM*,3\n");
        Assert.Single(lines);
        Assert.Equal(2, lines[0].Number);
        Assert.Equal("r1", lines[0].Name);
        Assert.Equal(new RuleFacts("ACM*", "roadster", null, null, true, null, 3, true), lines[0].Facts);
    }

    [Fact]
    public void A_number_in_the_year_column_fixes_the_year_and_vin_or_empty_reads_it_from_the_vin()
    {
        var lines = VinRuleCsv.Read("name,pattern,model,variant,year\na,ACM*,m,,2022\nb,ACN*,m,,VIN\nc,ACP*,m,,\n");
        Assert.Equal([2022, null, null], lines.Select(l => l.Facts!.ModelYear));
        Assert.Equal([false, true, true], lines.Select(l => l.Facts!.YearFromVin));
    }

    [Theory]
    [InlineData("enabled", "yes", true)]
    [InlineData("enabled", "1", true)]
    [InlineData("enabled", "TRUE", true)]
    [InlineData("enabled", "no", false)]
    [InlineData("enabled", "0", false)]
    [InlineData("enabled", "false", false)]
    public void Enabled_accepts_the_usual_spellings(string _, string text, bool expected)
    {
        var l = VinRuleCsv.Read($"name,pattern,model,enabled\na,ACM*,m,{text}\n").Single();
        Assert.Equal(expected, l.Facts!.Enabled);
    }

    [Fact]
    public void Bad_cells_produce_a_line_error_and_never_an_exception()
    {
        var lines = VinRuleCsv.Read("name,pattern,model,year,priority,enabled\n"
            + "ok,ACM*,m,,,\n"
            + "bad year,ACM*,m,20x4,,\n"
            + "bad prio,ACM*,m,,high,\n"
            + "bad enabled,ACM*,m,,,maybe\n"
            + "bad pattern,ACM,m,,,\n"
            + ",ACM*,m,,,\n"
            + "no model,ACM*,,,,\n");
        Assert.Null(lines[0].Error);
        Assert.Contains("year", lines[1].Error!, StringComparison.Ordinal);
        Assert.Contains("priority", lines[2].Error!, StringComparison.Ordinal);
        Assert.Contains("enabled", lines[3].Error!, StringComparison.Ordinal);
        Assert.Contains("Without a final *", lines[4].Error!, StringComparison.Ordinal);
        Assert.Contains("name is required", lines[5].Error!.ToLowerInvariant().Replace("a name", "name"), StringComparison.Ordinal);
        Assert.Contains("model", lines[6].Error!, StringComparison.Ordinal);
        Assert.Equal([2, 3, 4, 5, 6, 7, 8], lines.Select(l => l.Number));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("name,pattern\nr,ACM*\n")]                                // no model column
    [InlineData("name,pattern,model,colour\nr,ACM*,m,red\n")]            // unknown column
    [InlineData("name,pattern,model,model\nr,ACM*,m,m\n")]               // twice
    [InlineData("name,pattern,model\nr,\"ACM*,m\n")]                      // unterminated quote
    public void A_file_that_cannot_be_read_as_a_whole_is_a_format_error(string csv)
    {
        var ex = Assert.Throws<FormatException>(() => VinRuleCsv.Read(csv));
        Assert.NotEmpty(ex.Message);
    }
}

public sealed class KnowledgeDocumentTests
{
    private static KnowledgeDocument Doc() =>
        KnowledgeDocument.Create(Guid.NewGuid(), Guid.NewGuid(), "owner_manual", "roadster", null, null, null, "en", null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Creating_a_document_raises_one_event_and_a_manual_toc_marks_the_source()
    {
        var d = Doc();
        Assert.Single(d.PullEvents());
        var v = Guid.NewGuid();
        d.SetManualChapters([new Chapter("One", null, 1, 1, 2)], v, DateTimeOffset.UnixEpoch);
        Assert.Equal(ChapterSources.Manual, d.ChaptersSource);
        Assert.Equal(v, d.ChaptersIndexedVersionId);
        Assert.Equal([KnowledgeChaptersIndexed.EventName], d.PullEvents().Select(e => e.Name));
    }

    [Fact]
    public void Clearing_the_chapters_hands_the_document_back_to_automatic_indexing()
    {
        var d = Doc();
        d.PullEvents();
        d.SetManualChapters([new Chapter("One", null, 1, null, null)], Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        d.PullEvents();
        d.SetManualChapters([], Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Assert.Equal(ChapterSources.None, d.ChaptersSource);
        Assert.Null(d.ChaptersIndexedVersionId);
        Assert.Empty(d.Chapters);
        Assert.Contains(d.PullEvents(), e => e is KnowledgeReindexRequested { Force: false });
    }

    [Fact]
    public void An_indexing_result_with_no_chapters_is_recorded_as_none_and_manual_chapters_survive_a_new_version()
    {
        var d = Doc();
        var v1 = Guid.NewGuid();
        d.ApplyIndexed(v1, ChapterSources.Pdf, [], DateTimeOffset.UnixEpoch);
        Assert.Equal(ChapterSources.None, d.ChaptersSource);
        Assert.Equal(v1, d.ChaptersIndexedVersionId);

        d.SetManualChapters([new Chapter("Mine", null, 1, null, null)], v1, DateTimeOffset.UnixEpoch);
        var v2 = Guid.NewGuid();
        d.KeepManualFor(v2, DateTimeOffset.UnixEpoch);
        Assert.Equal(ChapterSources.Manual, d.ChaptersSource);
        Assert.Equal(["Mine"], d.Chapters.Select(c => c.Title));
        Assert.Equal(v2, d.ChaptersIndexedVersionId);
    }

    [Fact]
    public void Events_name_the_audit_trail_the_adr_promises()
    {
        Assert.Equal("knowledge.document.created", KnowledgeDocumentCreated.EventName);
        Assert.Equal("knowledge.document.updated", KnowledgeDocumentUpdated.EventName);
        Assert.Equal("knowledge.document.deleted", KnowledgeDocumentDeleted.EventName);
        Assert.Equal("knowledge.chapters.indexed", KnowledgeChaptersIndexed.EventName);
        Assert.Equal("knowledge.vin_rule.created", VinRuleCreated.EventName);
        Assert.Equal("knowledge.vin_rule.updated", VinRuleUpdated.EventName);
        Assert.Equal("knowledge.vin_rule.deleted", VinRuleDeleted.EventName);
        Assert.Equal("knowledge.vin_rules.imported", VinRulesImported.EventName);
    }

    [Fact]
    public void A_quiet_rule_raises_nothing_and_a_normal_one_raises_its_event()
    {
        var facts = new RuleFacts("ACM*", "roadster", null, null, true, null, 0, true);
        Assert.Empty(VinRule.Create(Guid.NewGuid(), "q", facts, DateTimeOffset.UnixEpoch, quiet: true).PullEvents());
        var r = VinRule.Create(Guid.NewGuid(), "loud", facts, DateTimeOffset.UnixEpoch);
        Assert.Equal([VinRuleCreated.EventName], r.PullEvents().Select(e => e.Name));
        r.Update("loud", facts with { Priority = 2 }, DateTimeOffset.UnixEpoch);
        Assert.Equal([VinRuleUpdated.EventName], r.PullEvents().Select(e => e.Name));
        Assert.True(facts with { Priority = 2 } is { } f && f.SameAs(r));
        r.MarkDeleted();
        Assert.Equal([VinRuleDeleted.EventName], r.PullEvents().Select(e => e.Name));
    }
}
