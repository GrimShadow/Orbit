using System.Globalization;
using System.Text;
using System.Text.Json;
using Dam.Shared.Audit;

namespace Dam.Application.Audit;

/// <summary>
/// ArcSight Common Event Format for audit rows (ADR-0031). One event per line:
/// <c>CEF:0|Orbit|DAM|&lt;version&gt;|&lt;action&gt;|&lt;name&gt;|&lt;severity&gt;|&lt;extension&gt;</c>. The signature id is the action (the event name); the name is a
/// readable form of it. Escaping follows the CEF rules: in the header <c>\</c> and <c>|</c>; in extension values <c>\</c> and <c>=</c>;
/// line breaks become <c>\n</c> in both, so one row is always one line.
/// </summary>
public static class CefFormatter
{
    public const string Vendor = "Orbit";
    public const string Product = "DAM";

    public static string Format(AuditEntryDto e, Guid tenantId, string version)
    {
        var sb = new StringBuilder("CEF:0|");
        sb.Append(Header(Vendor)).Append('|').Append(Header(Product)).Append('|').Append(Header(version)).Append('|')
          .Append(Header(e.Action)).Append('|').Append(Header(Name(e.Action))).Append('|')
          .Append(Severity(e.Action).ToString(CultureInfo.InvariantCulture)).Append('|');

        var first = true;
        void Add(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!first) sb.Append(' ');
            first = false;
            sb.Append(key).Append('=').Append(Extension(value));
        }
        Add("rt", e.OccurredAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Add("externalId", e.Id.ToString());
        Add("act", e.Action);
        Add("suser", e.ActorName);
        Add("suid", e.ActorId);
        Add("src", e.Ip);
        Add("cs1Label", "entityType"); Add("cs1", e.EntityType);
        Add("cs2Label", "entityId"); Add("cs2", e.EntityId?.ToString());
        Add("cs3Label", "tenantId"); Add("cs3", tenantId.ToString());
        Add("cs4Label", "hash"); Add("cs4", e.Hash);
        Add("cs5Label", "prevHash"); Add("cs5", e.PrevHash);
        Add("cn1Label", "chainNo"); Add("cn1", e.ChainNo.ToString(CultureInfo.InvariantCulture));
        Add("msg", Compact(e.After, 1000));
        return sb.ToString();
    }

    /// <summary>0-10. Security- and governance-relevant changes rate higher so a SIEM can alert on them; the rest is routine (3).</summary>
    public static int Severity(string action)
    {
        if (action.EndsWith(".rejected", StringComparison.Ordinal) || action.Contains("blocked", StringComparison.Ordinal)) return 7;
        if (action is "workflow.sla.breached" || action.StartsWith("access_rule.", StringComparison.Ordinal) || action.StartsWith("role.", StringComparison.Ordinal)
            || action.StartsWith("tenant.", StringComparison.Ordinal) || action.StartsWith("api_client.", StringComparison.Ordinal)
            || action.StartsWith("webhook.", StringComparison.Ordinal) || action.StartsWith("notification_channel.", StringComparison.Ordinal)
            || action == "asset.legal_hold.changed") return 6;
        if (action.EndsWith(".deleted", StringComparison.Ordinal) || action.Contains("purge", StringComparison.Ordinal)) return 5;
        return 3;
    }

    /// <summary>"asset.status.changed" becomes "Asset status changed".</summary>
    public static string Name(string action)
    {
        var words = action.Replace('.', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? action : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public static string Header(string value) => Escape(value, '|');

    public static string Extension(string value) => Escape(value, '=');

    private static string Escape(string value, char special)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (ch == special) sb.Append('\\');
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string? Compact(JsonElement? json, int max)
    {
        if (json is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        var s = json.Value.GetRawText();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>RFC 5424 syslog framing for the SIEM forwarder.</summary>
public static class SyslogFormatter
{
    public const int Facility = 16;   // local0

    /// <summary>CEF severity 0-10 mapped to a syslog severity (informational 6, notice 5, warning 4, critical 2).</summary>
    public static int SyslogSeverity(int cef) => cef switch { >= 9 => 2, >= 7 => 4, >= 4 => 5, _ => 6 };

    public static string Format(string cefLine, int cefSeverity, DateTimeOffset at, string hostname, string appName, string msgId)
    {
        var pri = (Facility * 8) + SyslogSeverity(cefSeverity);
        return $"<{pri}>1 {at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} {Token(hostname, 255)} {Token(appName, 48)} - {Token(msgId, 32)} - {cefLine}";
    }

    /// <summary>Header fields are printable ASCII without spaces; "-" stands for nothing.</summary>
    private static string Token(string value, int max)
    {
        var cleaned = new string(value.Where(c => c is > ' ' and < (char)127).Take(max).ToArray());
        return cleaned.Length == 0 ? "-" : cleaned;
    }

    /// <summary>RFC 6587 octet-counting frame: "&lt;length&gt; &lt;message&gt;", the length in bytes.</summary>
    public static byte[] OctetCounted(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var prefix = Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture) + " ");
        return [.. prefix, .. body];
    }

    /// <summary>RFC 6587 non-transparent framing: the message and a line feed.</summary>
    public static byte[] NewlineDelimited(string message) => Encoding.UTF8.GetBytes(message + "\n");
}

/// <summary>CSV for audit rows (RFC 4180 quoting). Cells that a spreadsheet would read as a formula get a leading apostrophe.</summary>
public static class CsvFormatter
{
    public static readonly string[] Columns =
        ["id", "chain_no", "occurred_at", "actor_id", "actor_user_id", "actor_name", "action", "entity_type", "entity_id", "ip", "before", "after", "prev_hash", "hash"];

    /// <summary>UTC with microseconds (the precision the database keeps) and a Z.</summary>
    public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static string HeaderRow() => string.Join(',', Columns);

    public static string Row(AuditEntryDto e) => string.Join(',',
        Cell(e.Id.ToString()), Cell(e.ChainNo.ToString(CultureInfo.InvariantCulture)), Cell(Iso(e.OccurredAt)),
        Cell(e.ActorId), Cell(e.ActorUserId?.ToString()), Cell(e.ActorName), Cell(e.Action), Cell(e.EntityType), Cell(e.EntityId?.ToString()), Cell(e.Ip),
        Cell(e.Before?.GetRawText()), Cell(e.After?.GetRawText()), Cell(e.PrevHash), Cell(e.Hash));

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
