using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>
/// Plumbing for the knowledge module's API tests (ADR-0039). Every test makes its own tenant; assets are made by the API and given the status the test needs straight in
/// the database (a published manual does not need a file for the mapping and the lookup to be tested). All names and codes are invented ("Acme").
/// </summary>
public abstract class KnowledgeApiBase(PostgresFixture pg) : AssetTestBase(pg)
{
    /// <summary>A fictional VIN: 17 characters, none of I, O or Q; the tenth ('R') is model year 2024.</summary>
    protected const string Vin = "ACMRS1AB4RA000123";

    protected async Task<(Guid Tenant, string Admin)> NewWorld()
    {
        var t = await NewTenantAsync("core", "automotive-sample");
        return (t, Admin(t));
    }

    /// <summary>Creates a manual (or another asset type) and sets its status; optional region tags (term codes) drive access control.</summary>
    protected async Task<Guid> NewManual(string admin, string title = "Owner's manual", string status = "published", string type = "manual", string[]? regions = null)
    {
        object metadata = regions is null ? new { description = new { en = title } } : new { description = new { en = title }, regions };
        var id = await NewAsset(admin, null, type, title, metadata, allowIncomplete: true);
        if (status != "draft") Sql($"UPDATE assets SET status = '{status}' WHERE id = '{id}'");
        return id;
    }

    protected async Task<JsonElement> CreateDoc(string admin, Guid asset, string model = "roadster", string? variant = null, int? from = null, int? to = null,
        string? lang = "en", string type = "owner_manual", string? theme = null)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin,
            new { assetId = asset, docType = type, model, variant, modelYearFrom = from, modelYearTo = to ?? from, language = lang, theme });
        Assert.True(r.Status == HttpStatusCode.Created, $"{r.Status}: {r.Body}");
        return r.Body;
    }

    protected async Task<JsonElement> CreateRule(string admin, string name = "roadster", string pattern = "ACM*", string model = "roadster", string? variant = null,
        int? priority = null, string? theme = null, int? year = null, bool? yearFromVin = null, bool? enabled = null)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules", admin, new { name, pattern, model, variant, modelYear = year, yearFromVin, theme, priority, enabled });
        Assert.True(r.Status == HttpStatusCode.Created, $"{r.Status}: {r.Body}");
        return r.Body;
    }

    protected Task<(HttpStatusCode Status, JsonElement Body)> Resolve(string token, string? vin = Vin, string? type = null, string? lang = null)
    {
        var q = new List<string>();
        if (vin is not null) q.Add("vin=" + Uri.EscapeDataString(vin));
        if (type is not null) q.Add("type=" + type);
        if (lang is not null) q.Add("lang=" + lang);
        return Call(HttpMethod.Get, "/api/v1/knowledge/resolve" + (q.Count > 0 ? "?" + string.Join('&', q) : ""), token);
    }

    protected static Guid DocOf(JsonElement resolved) => resolved.GetProperty("document").GetProperty("id").GetGuid();

    protected static string Dealer(Guid tenant, string region) =>
        ApiTestHost.TokenFor(tenant, Guid.NewGuid(), ["Dealer"], attributes: new() { ["region"] = [region] });

    /// <summary>Every built-in role as a token for the tenant: Dealer needs a region and ApiClient a channel, or they grant nothing.</summary>
    protected static Dictionary<string, string> TokensByRole(Guid tenant) => new()
    {
        ["Admin"] = Admin(tenant),
        ["Librarian"] = As(tenant, "Librarian"),
        ["Editor"] = As(tenant, "Editor"),
        ["Approver"] = As(tenant, "Approver"),
        ["Contributor"] = As(tenant, "Contributor"),
        ["Viewer"] = As(tenant, "Viewer"),
        ["Dealer"] = Dealer(tenant, "North"),
        ["ApiClient"] = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), ["ApiClient"], attributes: new() { ["channel"] = ["qr"] }),
        ["Ask Assist"] = As(tenant, "Ask Assist"),
    };

    protected static string Csv(params string[] lines) => string.Join('\n', lines) + "\n";
}
