using System.Globalization;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Knowledge;
using Dam.Shared.Knowledge;

namespace Dam.Application.Knowledge;

/// <summary>One record of a CSV file and the line it starts on (1-based; a quoted value may span lines).</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Cells);

/// <summary>A small RFC 4180 reader: comma separated, double quotes around values that hold commas, quotes or line breaks, doubled quotes inside; a byte-order mark and blank lines are skipped.</summary>
public static class CsvText
{
    /// <summary>Reads every record. Throws <see cref="FormatException"/> (with a message naming the line) for a quote that is never closed.</summary>
    public static List<CsvRecord> Parse(string text)
    {
        var records = new List<CsvRecord>();
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var quotedCell = false;
        var any = false;      // anything but blank space seen in the current record

        void EndCell() { cells.Add(quotedCell ? cell.ToString() : cell.ToString().Trim()); cell.Clear(); quotedCell = false; }
        void EndRecord()
        {
            EndCell();
            if (any) records.Add(new CsvRecord(recordLine, cells.ToList()));
            cells.Clear();
            any = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else { cell.Append(c); if (c == '\n') line++; }
                continue;
            }
            switch (c)
            {
                case '"' when cell.ToString().Trim().Length == 0:
                    cell.Clear();
                    inQuotes = true;
                    quotedCell = true;
                    any = true;
                    break;
                case ',':
                    EndCell();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRecord();
                    line++;
                    recordLine = line;
                    break;
                default:
                    cell.Append(c);
                    if (!char.IsWhiteSpace(c)) any = true;
                    break;
            }
        }
        if (inQuotes) throw new FormatException($"A quoted value that starts on line {recordLine} is never closed.");
        EndRecord();
        return records;
    }
}

/// <summary>Reads VIN rules from CSV (<c>name,pattern,model,variant,year,theme,priority,enabled</c>): pure, so the import's rules are tested without a database.</summary>
public static class VinRuleCsv
{
    public static readonly IReadOnlyList<string> Columns = ["name", "pattern", "model", "variant", "year", "theme", "priority", "enabled"];

    public sealed record Line(int Number, string Name, RuleFacts? Facts, string? Error);

    /// <summary>Reads the rows. A first row in which at least two cells are column names (name, pattern, ...) is a header (columns may then come in any order and
    /// optional ones may be left out); without a header the columns are in the order above. Throws <see cref="FormatException"/> when the file as a whole cannot be read.</summary>
    public static List<Line> Read(string csv)
    {
        var records = CsvText.Parse(csv);
        if (records.Count == 0) throw new FormatException("The file is empty.");

        var map = Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var first = records[0];
        var data = records;
        if (first.Cells.Count(c => Columns.Contains(c.Trim().ToLowerInvariant())) >= 2)
        {
            map = [];
            for (var i = 0; i < first.Cells.Count; i++)
            {
                var h = first.Cells[i].Trim().ToLowerInvariant();
                if (h.Length == 0) continue;
                if (!Columns.Contains(h)) throw new FormatException($"Unknown column '{first.Cells[i]}' in the header. The columns are: {string.Join(", ", Columns)}.");
                if (!map.TryAdd(h, i)) throw new FormatException($"The column '{h}' appears twice in the header.");
            }
            foreach (var required in new[] { "name", "pattern", "model" })
                if (!map.ContainsKey(required)) throw new FormatException($"The header must contain name, pattern and model; '{required}' is missing.");
            data = records.Skip(1).ToList();
        }

        var result = new List<Line>(data.Count);
        foreach (var rec in data)
        {
            string Cell(string column) => map.TryGetValue(column, out var i) && i < rec.Cells.Count ? rec.Cells[i] : "";
            result.Add(ReadLine(rec.Line, Cell));
        }
        return result;
    }

    private static Line ReadLine(int number, Func<string, string> cell)
    {
        var name = cell("name");
        var input = new VinRuleInput(name, cell("pattern"), cell("model"), Blank(cell("variant")), null, null, Blank(cell("theme")), null, null);

        // year: "vin" or nothing means read it from the VIN; a number fixes it.
        var year = cell("year");
        if (year.Length > 0 && !string.Equals(year, "vin", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y)) return new Line(number, name, null, $"year must be 'vin', empty or a year like 2026; got '{year}'.");
            input = input with { ModelYear = y };
        }
        var priority = cell("priority");
        if (priority.Length > 0)
        {
            if (!int.TryParse(priority, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p)) return new Line(number, name, null, $"priority must be a whole number; got '{priority}'.");
            input = input with { Priority = p };
        }
        var enabled = cell("enabled");
        if (enabled.Length > 0)
        {
            switch (enabled.ToLowerInvariant())
            {
                case "true" or "yes" or "1": input = input with { Enabled = true }; break;
                case "false" or "no" or "0": input = input with { Enabled = false }; break;
                default: return new Line(number, name, null, $"enabled must be true or false; got '{enabled}'.");
            }
        }

        var errors = KnowledgeRules.ValidateRule(input, out var facts, out var trimmed);
        if (errors.Count > 0) return new Line(number, trimmed, null, string.Join(" ", errors.SelectMany(e => e.Value)));
        return new Line(number, trimmed, facts, null);
    }

    private static string? Blank(string s) => s.Length == 0 ? null : s;
}

// ------------------------------------------------------------------------------------------------------------ import

/// <summary>
/// Creates or updates VIN rules from CSV, by name, in one transaction. With any invalid line nothing is changed unless <paramref name="Partial"/>; with
/// <paramref name="DryRun"/> nothing is changed at all and the answer says what would happen.
/// </summary>
public sealed record ImportVinRulesCommand(string? Csv, bool DryRun, bool Partial) : ICommand<VinImportResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class ImportVinRulesHandler(IRepository<VinRule> rules, IQueryExecutor q, IEventCollector events, ITenantContext tenant, IClock clock)
    : IRequestHandler<ImportVinRulesCommand, VinImportResultDto>
{
    public async Task<Result<VinImportResultDto>> Handle(ImportVinRulesCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Csv)) return Error.Validation(KnowledgeMapping.Fail("csv", "Send the rules as CSV text."));
        if (Encoding.UTF8.GetByteCount(c.Csv) > KnowledgeLimits.MaxImportBytes)
            return Error.Validation(KnowledgeMapping.Fail("csv", $"The file is larger than {KnowledgeLimits.MaxImportBytes / (1024 * 1024)} MB."));

        List<VinRuleCsv.Line> lines;
        try { lines = VinRuleCsv.Read(c.Csv); }
        catch (FormatException ex) { return Error.Validation(KnowledgeMapping.Fail("csv", ex.Message)); }

        var existing = (await q.ToListAsync(rules.Query(), ct)).GroupBy(r => r.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var errors = new List<VinImportErrorDto>();
        var seen = new Dictionary<string, int>();
        var valid = new List<(VinRuleCsv.Line Line, VinRule? Existing)>();
        foreach (var l in lines)
        {
            if (l.Error is not null) { errors.Add(new VinImportErrorDto(l.Number, l.Error)); continue; }
            var key = l.Name.ToLowerInvariant();
            if (seen.TryGetValue(key, out var firstLine)) { errors.Add(new VinImportErrorDto(l.Number, $"The name '{l.Name}' is already used on line {firstLine}.")); continue; }
            seen[key] = l.Number;
            valid.Add((l, existing.GetValueOrDefault(key)));
        }

        var newCount = valid.Count(v => v.Existing is null);
        if (existing.Count + newCount > KnowledgeLimits.MaxRulesPerTenant)
            return Error.Validation(KnowledgeMapping.Fail("csv", string.Create(CultureInfo.InvariantCulture,
                $"This import would bring the tenant to {existing.Count + newCount:N0} VIN rules; the limit is {KnowledgeLimits.MaxRulesPerTenant:N0}.")));

        var blocked = errors.Count > 0 && !c.Partial;
        int created = 0, updated = 0, skipped = 0;
        if (!blocked)
        {
            var now = clock.UtcNow;
            foreach (var (line, current) in valid)
            {
                if (current is null)
                {
                    created++;
                    if (!c.DryRun)
                    {
                        var rule = VinRule.Create(tenant.TenantId, line.Name, line.Facts!, now, quiet: true);
                        rules.Add(rule);
                    }
                }
                else if (line.Facts!.SameAs(current) && current.Name == line.Name) skipped++;
                else
                {
                    updated++;
                    if (!c.DryRun) current.Update(line.Name, line.Facts!, now, quiet: true);
                }
            }
            if (!c.DryRun && (created > 0 || updated > 0)) events.Raise(new VinRulesImported(created, updated, skipped, errors.Count, c.Partial));
        }
        return new VinImportResultDto(created, updated, skipped, errors.OrderBy(e => e.Line).ToList(), c.DryRun);
    }
}
