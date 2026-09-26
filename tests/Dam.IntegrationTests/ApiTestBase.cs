using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>Shared plumbing for API tests: an in-process API on the fixture database, tenants, tokens and JSON calls.</summary>
public abstract class ApiTestBase(PostgresFixture pg) : IAsyncLifetime
{
    protected PostgresFixture Pg { get; } = pg;
    protected WebApplicationFactory<Program> Api { get; private set; } = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        Api = new ApiTestHost(Pg).Factory();
        _http = Api.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await Api.DisposeAsync();
    }

    protected async Task<Guid> NewTenantAsync(params string[] templates)
    {
        var id = Uuid7.NewGuid();
        await using var scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Dam.Api.Auth.TenantOverride>().Value = id;
        var r = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new EnsureTenantCommand(id, "Tenant " + id.ToString("N")[..6], "t-" + id.ToString("N")[..12], templates));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Message : "");
        return id;
    }

    protected async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string path, string token, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    protected static string Admin(Guid tenant, Guid? subject = null) =>
        ApiTestHost.TokenFor(tenant, subject ?? Guid.NewGuid(), roles: ["Admin"], name: "Admin");

    protected static string As(Guid tenant, string role) => ApiTestHost.TokenFor(tenant, Guid.NewGuid(), roles: [role]);

    protected static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    protected long Count(Guid tenant, string sql)
    {
        using var c = Pg.RawAppConnection(tenant);
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    protected static Guid Id(JsonElement e, params string[] path)
    {
        foreach (var p in path) e = e.GetProperty(p);
        return e.GetGuid();
    }

    protected static object Field(string name, string type, Action<Dictionary<string, object?>>? more = null)
    {
        var d = new Dictionary<string, object?> { ["name"] = name, ["label"] = name, ["type"] = type, ["required"] = false, ["multi"] = false, ["searchable"] = true, ["facet"] = false, ["wholeNumbers"] = false };
        more?.Invoke(d);
        return d;
    }
}
