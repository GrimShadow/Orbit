using System.Net;
using System.Text;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>VIN rules (ADR-0039 decisions 3 and 10): CRUD and limits, the rule tester, and the CSV import with its dry run and partial modes.</summary>
[Collection("pg")]
public sealed class KnowledgeRuleTests(PostgresFixture pg) : KnowledgeApiBase(pg)
{
    private const string Header = "name,pattern,model,variant,year,theme,priority,enabled";

    private async Task<JsonElement> Import(string admin, string csv, string query = "", HttpStatusCode expected = HttpStatusCode.OK)
    {
        var r = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import" + query, admin, csv);
        Assert.True(r.Status == expected, $"{r.Status}: {r.Body}");
        return r.Body;
    }

    private static (int Created, int Updated, int Skipped, int Errors) Counts(JsonElement r) =>
        (r.GetProperty("created").GetInt32(), r.GetProperty("updated").GetInt32(), r.GetProperty("skipped").GetInt32(), r.GetProperty("errors").GetArrayLength());

    // ---- CRUD -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rule_is_created_read_changed_and_deleted()
    {
        var (t, admin) = await NewWorld();
        var r = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin,
            new { name = "  Roadster GT  ", pattern = " acmrs1* ", model = "Roadster", variant = "GT", theme = "ICE", priority = 7 });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        var rule = r.Body;
        Assert.Equal("Roadster GT", rule.GetProperty("name").GetString());
        Assert.Equal("ACMRS1*", rule.GetProperty("pattern").GetString());
        Assert.Equal("roadster", rule.GetProperty("model").GetString());
        Assert.Equal("gt", rule.GetProperty("variant").GetString());
        Assert.Equal("ice", rule.GetProperty("theme").GetString());
        Assert.Equal(7, rule.GetProperty("priority").GetInt32());
        Assert.True(rule.GetProperty("enabled").GetBoolean());
        Assert.True(rule.GetProperty("yearFromVin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("modelYear").ValueKind);
        var id = rule.GetProperty("id").GetGuid();

        var put = await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{id}", admin,
            new { name = "Roadster GT", pattern = "ACMRS?*", model = "roadster", modelYear = 2022, priority = -3, enabled = false });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("ACMRS?*", put.Body.GetProperty("pattern").GetString());
        Assert.Equal(2022, put.Body.GetProperty("modelYear").GetInt32());
        Assert.False(put.Body.GetProperty("yearFromVin").GetBoolean());                       // a fixed year turns reading it off
        Assert.False(put.Body.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, put.Body.GetProperty("variant").ValueKind);
        Assert.Equal(-3, put.Body.GetProperty("priority").GetInt32());

        var list = (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules", admin)).Body;
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextCursor").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/vin-rules/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/vin-rules/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{id}", admin, new { name = "x", pattern = "ACM*", model = "m" })).Status);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM vin_rules"));
    }

    [Fact]
    public async Task Invalid_rules_are_refused_field_by_field_with_a_message_that_says_what_to_do()
    {
        var (_, admin) = await NewWorld();
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        string Msg(JsonElement e, string field) => e.GetProperty(field)[0].GetString()!;
        Assert.True((await Errors(new { pattern = "ACM*", model = "m" })).TryGetProperty("name", out _));
        Assert.True((await Errors(new { name = new string('n', 201), pattern = "ACM*", model = "m" })).TryGetProperty("name", out _));
        Assert.Contains("Add a *", Msg(await Errors(new { name = "n", pattern = "ACM", model = "m" }), "pattern"), StringComparison.Ordinal);
        Assert.Contains("I, O and Q", Msg(await Errors(new { name = "n", pattern = "ACMI*", model = "m" }), "pattern"), StringComparison.Ordinal);
        Assert.Contains("last character", Msg(await Errors(new { name = "n", pattern = "AC*M", model = "m" }), "pattern"), StringComparison.Ordinal);
        Assert.Contains("backwards", Msg(await Errors(new { name = "n", pattern = "AC[N-M]*", model = "m" }), "pattern"), StringComparison.Ordinal);
        Assert.Contains("at most 17", Msg(await Errors(new { name = "n", pattern = "??????????????????*", model = "m" }), "pattern"), StringComparison.Ordinal);
        Assert.True((await Errors(new { name = "n", pattern = "ACM*" })).TryGetProperty("model", out _));
        Assert.True((await Errors(new { name = "n", pattern = "ACM*", model = "Bad Model" })).TryGetProperty("model", out _));
        Assert.True((await Errors(new { name = "n", pattern = "ACM*", model = "m", variant = "no spaces" })).TryGetProperty("variant", out _));
        Assert.True((await Errors(new { name = "n", pattern = "ACM*", model = "m", theme = "a/b" })).TryGetProperty("theme", out _));
        Assert.True((await Errors(new { name = "n", pattern = "ACM*", model = "m", modelYear = 1979 })).TryGetProperty("modelYear", out _));
        Assert.True((await Errors(new { name = "n", pattern = "ACM*", model = "m", modelYear = 2020, yearFromVin = true })).TryGetProperty("yearFromVin", out _));
        Assert.True((await Errors(new { name = "n", pattern = "ACM*", model = "m", priority = 10_001 })).TryGetProperty("priority", out _));
    }

    [Fact]
    public async Task A_pattern_counts_positions_a_set_is_one_and_the_final_star_is_none()
    {
        var (_, admin) = await NewWorld();
        async Task<HttpStatusCode> Try(string pattern, string name) =>
            (await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, new { name, pattern, model = "m" })).Status;
        Assert.Equal(HttpStatusCode.Created, await Try("ACM*", "three"));
        Assert.Equal(HttpStatusCode.Created, await Try("[ABC][A-C]ACM*", "sets are one position each"));
        Assert.Equal(HttpStatusCode.Created, await Try("[A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C]", "seventeen sets"));
        Assert.Equal(HttpStatusCode.Created, await Try("?????????????????*", "seventeen and a star"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("??????????????????*", "eighteen"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("[A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C][A-C]*", "eighteen sets"));
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case_and_a_rule_can_keep_its_own_name()
    {
        var (_, admin) = await NewWorld();
        var a = (await CreateRule(admin, "Alpha", "AAA*", "m")).GetProperty("id").GetGuid();
        await CreateRule(admin, "Beta", "BBB*", "m");
        var dup = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, new { name = "ALPHA", pattern = "CCC*", model = "m" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.Status);
        Assert.Contains("already exists", dup.Body.GetProperty("errors").GetProperty("name")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{a}", admin, new { name = "alpha", pattern = "AAB*", model = "m" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{a}", admin, new { name = "BETA", pattern = "AAB*", model = "m" })).Status);
    }

    [Fact]
    public async Task The_list_pages_by_name_with_a_default_of_100_a_maximum_of_500_and_filters_by_text_and_model()
    {
        var (_, admin) = await NewWorld();
        var csv = new StringBuilder(Header + "\n");
        for (var i = 0; i < 130; i++) csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"Rule {i:D3},AC{i:D3}*,{(i % 2 == 0 ? "roadster" : "spark")},,,,,\n");
        await Import(admin, csv.ToString());

        var first = (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules", admin)).Body;
        Assert.Equal(100, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.Equal("rule 099", cursor);
        Assert.Equal("Rule 000", first.GetProperty("items")[0].GetProperty("name").GetString());
        var second = (await Call(HttpMethod.Get, $"/api/v1/knowledge/vin-rules?cursor={Uri.EscapeDataString(cursor!)}", admin)).Body;
        Assert.Equal(30, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal("Rule 100", second.GetProperty("items")[0].GetProperty("name").GetString());

        Assert.Equal(130, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?limit=500", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(130, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?limit=9999", admin)).Body.GetProperty("items").GetArrayLength());      // capped, not an error
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?limit=1", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(65, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?model=spark&limit=200", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?q=rule%20042", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?model=Bad%20Model", admin)).Status);
    }

    // ---- the tester -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tester_names_the_chosen_rule_the_vehicle_and_why_each_rule_matched_or_not()
    {
        var (_, admin) = await NewWorld();
        var winner = (await CreateRule(admin, "winner", "ACMRS*", "roadster", "gt", priority: 2, theme: "ice")).GetProperty("id").GetGuid();
        await CreateRule(admin, "loser", "ACM*", "voyager", priority: 1);
        await CreateRule(admin, "elsewhere", "XYZ*", "spark");
        await CreateRule(admin, "off", "ACM*", "spark", priority: 9, enabled: false);

        var r = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", admin, new { vin = "acmrs1ab4ra000123" });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.Body.GetProperty("valid").GetBoolean());
        Assert.Equal(Vin, r.Body.GetProperty("vin").GetString());
        Assert.Equal(winner, r.Body.GetProperty("chosen").GetProperty("ruleId").GetGuid());
        Assert.Equal("winner", r.Body.GetProperty("chosen").GetProperty("name").GetString());
        var vehicle = r.Body.GetProperty("vehicle");
        Assert.Equal("roadster", vehicle.GetProperty("model").GetString());
        Assert.Equal("gt", vehicle.GetProperty("variant").GetString());
        Assert.Equal(2024, vehicle.GetProperty("modelYear").GetInt32());
        Assert.Equal("ice", vehicle.GetProperty("theme").GetString());

        var rows = r.Body.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(["winner", "loser"], rows.Where(c => c.GetProperty("matched").GetBoolean()).Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("winner", rows[0].GetProperty("name").GetString());
        Assert.Equal(5, rows[0].GetProperty("specificity").GetInt32());
        Assert.Equal(2, rows[0].GetProperty("priority").GetInt32());
        Assert.Contains("higher priority", rows[1].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("Character 1", rows.Single(c => c.GetProperty("name").GetString() == "elsewhere").GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("switched off", rows.Single(c => c.GetProperty("name").GetString() == "off").GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tester_answers_200_with_valid_false_and_a_reason_for_a_malformed_vin_and_with_no_choice_when_nothing_matches()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin, "elsewhere", "XYZ*", "spark");
        foreach (var bad in new[] { "SHORT", "ACMRS1AB4RA00012I", "", "ACMRS1AB4RA 00123" })
        {
            var r = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", admin, new { vin = bad });
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.False(r.Body.GetProperty("valid").GetBoolean());
            Assert.NotEmpty(r.Body.GetProperty("reason").GetString()!);
            Assert.Equal(JsonValueKind.Null, r.Body.GetProperty("chosen").ValueKind);
            Assert.Equal(JsonValueKind.Null, r.Body.GetProperty("vehicle").ValueKind);
            Assert.Equal(0, r.Body.GetProperty("candidates").GetArrayLength());
        }
        var none = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", admin, new { vin = Vin });
        Assert.True(none.Body.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, none.Body.GetProperty("chosen").ValueKind);
        Assert.Equal(1, none.Body.GetProperty("candidates").GetArrayLength());
        Assert.False(none.Body.GetProperty("candidates")[0].GetProperty("matched").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", admin, new { })).Status);
    }

    // ---- import ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_import_creates_updates_and_skips_by_name_and_a_second_run_changes_nothing()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin, "existing same", "AAA*", "m", priority: 3);
        await CreateRule(admin, "existing changed", "BBB*", "m");
        var csv = Csv(Header,
            "new one,CCC*,roadster,gt,vin,ice,5,true",
            "existing same,AAA*,m,,,,3,true",
            "EXISTING CHANGED,BBC*,m,base,2021,ev,1,false");
        var r = await Import(admin, csv);
        Assert.Equal((1, 1, 1, 0), Counts(r));
        Assert.False(r.GetProperty("dryRun").GetBoolean());
        Assert.Equal(3, Count(t, "SELECT count(*) FROM vin_rules"));

        var changed = (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?q=changed", admin)).Body.GetProperty("items")[0];
        Assert.Equal("EXISTING CHANGED", changed.GetProperty("name").GetString());           // a name's spelling follows the file
        Assert.Equal("BBC*", changed.GetProperty("pattern").GetString());
        Assert.Equal(2021, changed.GetProperty("modelYear").GetInt32());
        Assert.False(changed.GetProperty("yearFromVin").GetBoolean());
        Assert.False(changed.GetProperty("enabled").GetBoolean());
        var created = (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?q=new%20one", admin)).Body.GetProperty("items")[0];
        Assert.True(created.GetProperty("yearFromVin").GetBoolean());
        Assert.Equal("ice", created.GetProperty("theme").GetString());
        Assert.Equal(5, created.GetProperty("priority").GetInt32());

        Assert.Equal((0, 0, 3, 0), Counts(await Import(admin, csv)));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'knowledge.vin_rules.imported'"));      // the no-op import wrote nothing
    }

    [Fact]
    public async Task A_header_is_optional_may_reorder_columns_and_quoting_and_windows_line_endings_work()
    {
        var (_, admin) = await NewWorld();
        Assert.Equal((1, 0, 0, 0), Counts(await Import(admin, "no header,AAA*,roadster,gt,vin,ice,2,true\r\n")));
        Assert.Equal((1, 0, 0, 0), Counts(await Import(admin, "﻿model,name,pattern\r\nspark,\"Quoted, with comma\",BBB*\r\n")));
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?q=comma", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal((1, 0, 0, 0), Counts(await Import(admin, "name,pattern,model,enabled\nyes spelling,CCC*,m,yes\n")));
    }

    [Fact]
    public async Task One_bad_line_stops_the_whole_import_and_every_bad_line_is_reported_by_number()
    {
        var (t, admin) = await NewWorld();
        var csv = Csv(Header,
            "good,AAA*,m,,,,,",
            "bad pattern,AAA,m,,,,,",
            "good two,BBB*,m,,,,,",
            "bad year,CCC*,m,,20x4,,,",
            "good three,DDD*,m,,,,,",
            "good,EEE*,m,,,,,");
        var r = await Import(admin, csv);
        Assert.Equal((0, 0, 0, 3), Counts(r));
        var errors = r.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal([3, 5, 7], errors.Select(e => e.GetProperty("line").GetInt32()));
        Assert.Contains("Without a final *", errors[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("year", errors[1].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("already used on line 2", errors[2].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM vin_rules"));                         // nothing was created
        Assert.Equal(0, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'knowledge.vin_rules.imported'"));
    }

    [Fact]
    public async Task With_partial_the_good_lines_are_applied_and_the_bad_ones_reported()
    {
        var (t, admin) = await NewWorld();
        var csv = Csv(Header, "good,AAA*,m,,,,,", "bad,AAA,m,,,,,", "good two,BBB*,m,,,,,");
        var r = await Import(admin, csv, "?partial=true");
        Assert.Equal((2, 0, 0, 1), Counts(r));
        Assert.Equal(3, r.GetProperty("errors")[0].GetProperty("line").GetInt32());
        Assert.Equal(2, Count(t, "SELECT count(*) FROM vin_rules"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'knowledge.vin_rules.imported'"));
    }

    [Fact]
    public async Task A_dry_run_reports_exactly_what_would_happen_and_changes_nothing()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin, "existing", "AAA*", "m");
        var csv = Csv(Header, "existing,AAB*,m,,,,,", "fresh,CCC*,m,,,,,", "fresh two,DDD*,m,,,,,", "broken,CCC,m,,,,,");
        var dry = await Import(admin, csv, "?dryRun=true&partial=true");
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal((2, 1, 0, 1), Counts(dry));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM vin_rules"));
        Assert.Equal("AAA*", (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules", admin)).Body.GetProperty("items")[0].GetProperty("pattern").GetString());

        // the same answer as the real thing, which then happens
        var real = await Import(admin, csv, "?partial=true");
        Assert.Equal(Counts(dry), Counts(real));
        Assert.Equal(3, Count(t, "SELECT count(*) FROM vin_rules"));

        // a dry run of a file with errors, without partial, says nothing would be done
        Assert.Equal((0, 0, 0, 1), Counts(await Import(admin, Csv(Header, "ok,EEE*,m,,,,,", "broken,CCC,m,,,,,"), "?dryRun=true")));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_as_a_whole_is_a_422_on_csv()
    {
        var (_, admin) = await NewWorld();
        async Task<string> Refused(string body)
        {
            var r = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors").GetProperty("csv")[0].GetString()!;
        }
        Assert.Contains("CSV", await Refused(""), StringComparison.Ordinal);
        Assert.Contains("CSV", await Refused("   \n  "), StringComparison.Ordinal);
        Assert.Contains("'model' is missing", await Refused("name,pattern\nr,ACM*\n"), StringComparison.Ordinal);
        Assert.Contains("Unknown column 'colour'", await Refused("name,pattern,model,colour\nr,ACM*,m,red\n"), StringComparison.Ordinal);
        Assert.Contains("never closed", await Refused("name,pattern,model\nr,\"ACM*,m\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csv_may_also_be_sent_inside_a_json_body()
    {
        var (_, admin) = await NewWorld();
        var ok = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, JsonSerializer.Serialize(new { csv = Csv(Header, "from json,AAA*,m,,,,,") }), "application/json");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal((1, 0, 0, 0), Counts(ok.Body));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, "{\"nothing\":1}", "application/json")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, "not json", "application/json")).Status);
        // plain text works too
        Assert.Equal(HttpStatusCode.OK, (await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, Csv(Header, "as text,BBB*,m,,,,,"), "text/plain")).Status);
    }

    // ---- limits ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_over_two_megabytes_is_refused()
    {
        var (t, admin) = await NewWorld();
        var big = Header + "\n" + string.Concat(Enumerable.Repeat("x,AAA*,m,,,,,\n", 200_000));          // about 2.8 MB
        var r = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, big);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Contains("2 MB", r.Body.GetProperty("errors").GetProperty("csv")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM vin_rules"));
    }

    [Fact]
    public async Task A_tenant_holds_at_most_5000_rules_and_every_way_in_says_so_clearly()
    {
        var (t, admin) = await NewWorld();
        string Rules(int from, int to) => Header + "\n" + string.Concat(Enumerable.Range(from, to - from).Select(i =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"rule {i},{Prefix(i)}*,m,,,,,\n")));
        // Prefixes of three letters: 5,000 distinct ones are easy to make and the pattern itself is not what is being tested.
        const string Letters = "ABCDEFGHJKLMNPRSTUVWXYZ";                               // no I, O or Q
        static string Prefix(int i) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Letters[i / 529 % 23]}{Letters[i / 23 % 23]}{Letters[i % 23]}");

        var five = await Import(admin, Rules(0, 5000));
        Assert.Equal((5000, 0, 0, 0), Counts(five));
        Assert.Equal(5000, Count(t, "SELECT count(*) FROM vin_rules"));

        var create = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, new { name = "one too many", pattern = "ZZZ*", model = "m" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.Status);
        Assert.Contains("at most 5,000 VIN rules", create.Body.GetProperty("errors").GetProperty("name")[0].GetString(), StringComparison.Ordinal);

        var over = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, Csv(Header, "one too many,ZZZ*,m,,,,,"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.Status);
        Assert.Contains("5,001", over.Body.GetProperty("errors").GetProperty("csv")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(5000, Count(t, "SELECT count(*) FROM vin_rules"));

        // changing rules that are already there does not grow the set, and a deleted one makes room
        Assert.Equal((0, 1, 0, 0), Counts(await Import(admin, Csv(Header, "rule 0,ZZA*,m,,,,,"))));
        var first = (await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules?limit=1", admin)).Body.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/vin-rules/{first}", admin)).Status);
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, new { name = "now it fits", pattern = "ZZZ*", model = "m" })).Status);
    }
}
