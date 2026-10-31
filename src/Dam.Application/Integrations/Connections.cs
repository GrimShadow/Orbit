using System.Diagnostics;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Integrations;
using Dam.Shared.Identity;
using Dam.Shared.Integrations;
using FluentValidation;

namespace Dam.Application.Integrations;

internal static class IntegrationMapping
{
    public static ConnectorDto ToDto(ConnectorDescriptor d) =>
        new(d.Key, d.Name, d.Description, d.Category, d.Status, d.AuthStrategies, d.DefaultAuthStrategy, d.Capabilities, d.Fields.Select(ToDto).ToList());

    public static ConnectorFieldDto ToDto(ConnectorField f) =>
        new(f.Key, f.Label, f.Type, f.Required, f.Help, f.Placeholder, f.Group, (f.Options ?? []).Select(o => new ConnectorFieldOptionDto(o.Value, o.Label)).ToList(), f.Default,
            f.ShowWhen is null ? null : new ConnectorShowWhenDto(f.ShowWhen.Field, f.ShowWhen.Values));

    /// <summary>The settings a request sent, as JSON values. (The API model types them as plain objects so the OpenAPI document and its TypeScript types read as a dictionary of unknowns.)</summary>
    public static Dictionary<string, JsonElement>? Elements(Dictionary<string, object?>? config) =>
        config?.ToDictionary(kv => kv.Key, kv => kv.Value is JsonElement e ? e : JsonSerializer.SerializeToElement(kv.Value));

    /// <summary>The connection as the API shows it. A secret appears only as whether it is set and its four-character hint.</summary>
    public static ConnectionDto ToDto(IntegrationConnection c, ConnectorDescriptor? d)
    {
        var secretKeys = d is null ? c.Secrets.Keys : d.Fields.Where(f => f.IsSecret).Select(f => f.Key);
        var secrets = secretKeys.ToDictionary(k => k, k => c.Secrets.TryGetValue(k, out var s) ? new SecretStateDto(true, s.Hint) : new SecretStateDto(false, null));
        return new ConnectionDto(c.Id, c.Connector, c.Name, c.Enabled, c.Config.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), secrets, c.Status, c.StatusDetail, c.LastTestedAt, c.LastOkAt,
            c.TokenExpiresAt, c.CreatedAt, c.UpdatedAt);
    }

    public static ActivityDto ToDto(IntegrationCall c) => new(c.Id, c.Kind, c.Method, c.Target, c.StatusCode, c.Ok, c.DurationMs, c.Error, c.CreatedAt);

    /// <summary>The values of the URL-typed settings (and an absolute sign-in address) that must pass the outbound URL guard, by field key.</summary>
    public static IEnumerable<KeyValuePair<string, string>> GuardedUrls(ConnectorDescriptor d, IReadOnlyDictionary<string, JsonElement> config)
    {
        foreach (var f in d.Fields.Where(f => f.Type == FieldTypes.Url))
            if (config.TryGetValue(f.Key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } url) yield return new(f.Key, url);
        if (config.TryGetValue(FieldKeys.LoginUrl, out var login) && login.ValueKind == JsonValueKind.String && ConnectorUrls.IsAbsolute(login.GetString())) yield return new(FieldKeys.LoginUrl, login.GetString()!);
    }
}

// ----------------------------------------------------------------------------------------------------------------------------------- catalogue

public sealed record ListConnectorsQuery : IQuery<IReadOnlyList<ConnectorDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class ListConnectorsHandler(ConnectorRegistry registry) : IRequestHandler<ListConnectorsQuery, IReadOnlyList<ConnectorDto>>
{
    public Task<Result<IReadOnlyList<ConnectorDto>>> Handle(ListConnectorsQuery r, CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<ConnectorDto>>>(registry.All.Select(c => IntegrationMapping.ToDto(c.Descriptor)).ToList());
}

// ------------------------------------------------------------------------------------------------------------------------------------- reads

public sealed record ListConnectionsQuery : IQuery<IReadOnlyList<ConnectionDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class ListConnectionsHandler(IRepository<IntegrationConnection> connections, ConnectorRegistry registry, IQueryExecutor q)
    : IRequestHandler<ListConnectionsQuery, IReadOnlyList<ConnectionDto>>
{
    public async Task<Result<IReadOnlyList<ConnectionDto>>> Handle(ListConnectionsQuery r, CancellationToken ct) =>
        (await q.ToListAsync(connections.Query().OrderBy(c => c.Name).ThenBy(c => c.Id), ct)).Select(c => IntegrationMapping.ToDto(c, registry.Find(c.Connector)?.Descriptor)).ToList();
}

public sealed record GetConnectionQuery(Guid Id) : IQuery<ConnectionDto>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class GetConnectionHandler(IRepository<IntegrationConnection> connections, ConnectorRegistry registry, IQueryExecutor q) : IRequestHandler<GetConnectionQuery, ConnectionDto>
{
    public async Task<Result<ConnectionDto>> Handle(GetConnectionQuery r, CancellationToken ct)
    {
        var c = await q.FirstOrDefaultAsync(connections.Query().Where(x => x.Id == r.Id), ct);
        return c is null ? Error.NotFound("Connection not found.") : IntegrationMapping.ToDto(c, registry.Find(c.Connector)?.Descriptor);
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------- create

public sealed record CreateConnectionCommand(ConnectionInput Input) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class CreateConnectionValidator : AbstractValidator<CreateConnectionCommand>
{
    public CreateConnectionValidator(ConnectorRegistry registry)
    {
        // A rule on the whole command, so the failures keep the keys the API promises (name, connector, config.<field>, secrets.<field>).
        RuleFor(x => x).Custom((c, ctx) =>
        {
            var input = c.Input;
            if (ConnectionRules.CheckName(input.Name) is { } nameProblem) ctx.AddFailure("name", nameProblem);
            var connector = registry.Find(input.Connector?.Trim());
            if (connector is null) { ctx.AddFailure("connector", string.IsNullOrWhiteSpace(input.Connector) ? "Choose a connector." : "Unknown connector."); return; }
            var d = connector.Descriptor;
            if (!d.CanBeCreated) { ctx.AddFailure("connector", $"{d.Name} is planned and cannot be connected yet."); return; }

            var check = ConnectionRules.CheckInput(d, IntegrationMapping.Elements(input.Config), input.Secrets, input.ClearSecrets);
            var errors = new FieldErrors();
            errors.AddAll(check.Errors);
            errors.AddMissing(ConnectionRules.CheckComplete(d, check.Config, check.Secrets.ContainsKey));
            foreach (var (key, messages) in errors.ToDictionary()) foreach (var m in messages) ctx.AddFailure(key, m);
        });
    }
}

public sealed class CreateConnectionHandler(
    IRepository<IntegrationConnection> connections, ConnectorRegistry registry, ISecretProtector secrets, IOutboundUrlGuard guard, IAuthorizationService authz,
    ITenantContext tenant, IQueryExecutor q, IClock clock) : IRequestHandler<CreateConnectionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateConnectionCommand c, CancellationToken ct)
    {
        var input = c.Input;
        var d = registry.Find(input.Connector!.Trim())!.Descriptor;   // the validator has already refused an unknown or planned connector
        var check = ConnectionRules.CheckInput(d, IntegrationMapping.Elements(input.Config), input.Secrets, input.ClearSecrets);
        var name = input.Name.Trim();

        if (await q.CountAsync(connections.Query(), ct) >= ConnectionRules.MaxPerTenant)
            return Error.Validation(ContentMapping.Fail("name", $"At most {ConnectionRules.MaxPerTenant} connections."));
        var lower = name.ToLowerInvariant();
        if (await q.AnyAsync(connections.Query().Where(x => x.Name.ToLower() == lower), ct)) return Error.Conflict($"A connection named '{name}' already exists.");
        if (await UrlProblemsAsync(guard, d, check.Config, null, ct) is { Count: > 0 } urlProblems) return Error.Validation(urlProblems);

        var id = Uuid7.NewGuid();
        var stored = check.Secrets.ToDictionary(kv => kv.Key, kv => new StoredSecret(secrets.Protect(kv.Value, IntegrationSecretPurposes.Field(id, kv.Key)), SecretRules.Hint(kv.Value)));
        var connection = IntegrationConnection.Create(id, tenant.TenantId, d.Key, name, input.Enabled, check.Config, stored, (await authz.GetProfileAsync(ct)).UserId, clock.UtcNow, d.SavedOnlyDetail);
        connections.Add(connection);
        return id;
    }

    /// <summary>
    /// Runs the outbound URL guard over the URL settings that are new or changed. Keys are <c>config.&lt;field&gt;</c>. A connector that makes no calls (it only stores its settings) is not
    /// guarded at save: nothing is ever requested, and such a server is usually on a private network. The scheme and length rules still apply (see <see cref="ConnectionRules.CheckInput"/>).
    /// </summary>
    public static async Task<Dictionary<string, string[]>> UrlProblemsAsync(
        IOutboundUrlGuard guard, ConnectorDescriptor d, IReadOnlyDictionary<string, JsonElement> config, IReadOnlyDictionary<string, JsonElement>? existing, CancellationToken ct)
    {
        var problems = new Dictionary<string, string[]>();
        if (!d.MakesCalls) return problems;
        foreach (var (key, url) in IntegrationMapping.GuardedUrls(d, config))
        {
            if (existing is not null && existing.TryGetValue(key, out var old) && old.ValueKind == JsonValueKind.String && old.GetString() == url) continue;
            if (await guard.CheckAsync(url, ct) is { } problem) problems["config." + key] = [problem];
        }
        return problems;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------- update

public sealed record UpdateConnectionCommand(Guid Id, ConnectionInput Input) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class UpdateConnectionValidator : AbstractValidator<UpdateConnectionCommand>
{
    public UpdateConnectionValidator()
    {
        RuleFor(x => x).Custom((c, ctx) =>
        {
            if (ConnectionRules.CheckName(c.Input.Name) is { } nameProblem) ctx.AddFailure("name", nameProblem);
        });
    }
}

public sealed class UpdateConnectionHandler(
    IRepository<IntegrationConnection> connections, ConnectorRegistry registry, ISecretProtector secrets, IOutboundUrlGuard guard, IQueryExecutor q, IClock clock)
    : IRequestHandler<UpdateConnectionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateConnectionCommand c, CancellationToken ct)
    {
        var connection = await q.FirstOrDefaultAsync(connections.Query().Where(x => x.Id == c.Id), ct);
        if (connection is null) return Error.NotFound("Connection not found.");
        var input = c.Input;
        if (registry.Find(connection.Connector)?.Descriptor is not { } d)
            return Error.Validation(ContentMapping.Fail("connector", "This connector is no longer available."));
        if (!string.IsNullOrWhiteSpace(input.Connector) && !string.Equals(input.Connector.Trim(), connection.Connector, StringComparison.Ordinal))
            return Error.Validation(ContentMapping.Fail("connector", "A connection's connector cannot be changed. Create a new connection instead."));

        var name = input.Name.Trim();
        var lower = name.ToLowerInvariant();
        if (await q.AnyAsync(connections.Query().Where(x => x.Id != connection.Id && x.Name.ToLower() == lower), ct)) return Error.Conflict($"A connection named '{name}' already exists.");

        var check = ConnectionRules.CheckInput(d, IntegrationMapping.Elements(input.Config), input.Secrets, input.ClearSecrets);
        var errors = new FieldErrors();
        errors.AddAll(check.Errors);
        var kept = connection.Secrets.Keys.Where(k => !check.Clear.Contains(k)).ToHashSet();
        errors.AddMissing(ConnectionRules.CheckComplete(d, check.Config, k => kept.Contains(k) || check.Secrets.ContainsKey(k)));
        if (errors.Any) return Error.Validation(errors.ToDictionary());
        if (await CreateConnectionHandler.UrlProblemsAsync(guard, d, check.Config, connection.Config, ct) is { Count: > 0 } urlProblems) return Error.Validation(urlProblems);

        var stored = new Dictionary<string, StoredSecret>(connection.Secrets);
        foreach (var key in check.Clear) stored.Remove(key);
        foreach (var (key, value) in check.Secrets) stored[key] = new StoredSecret(secrets.Protect(value, IntegrationSecretPurposes.Field(connection.Id, key)), SecretRules.Hint(value));
        var changedSecrets = check.Secrets.Keys.Concat(check.Clear.Where(connection.Secrets.ContainsKey)).Order(StringComparer.Ordinal).ToList();

        connection.Update(name, input.Enabled, check.Config, stored, changedSecrets, clock.UtcNow, d.SavedOnlyDetail);
        return connection.Id;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------- delete

public sealed record DeleteConnectionCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class DeleteConnectionHandler(IRepository<IntegrationConnection> connections, IQueryExecutor q) : IRequestHandler<DeleteConnectionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteConnectionCommand c, CancellationToken ct)
    {
        var connection = await q.FirstOrDefaultAsync(connections.Query().Where(x => x.Id == c.Id), ct);
        if (connection is null) return Error.NotFound("Connection not found.");
        connection.MarkDeleted();
        connections.Remove(connection);   // its activity goes with it (foreign key cascade)
        return connection.Id;
    }
}

// ------------------------------------------------------------------------------------------------------------------------------------- test

/// <summary>
/// Tests a connection: signs in and, when a test path is set, makes one GET of it (ADR-0040 decision 6). A query on purpose: it runs with no database transaction open,
/// because the calls to the other system take as long as they take (ADR-0038). The result is recorded by <see cref="RecordConnectionTestCommand"/> afterwards.
/// </summary>
public sealed record TestConnectionQuery(Guid Id) : IQuery<TestResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class TestConnectionHandler(
    IRepository<IntegrationConnection> connections, ConnectorRegistry registry, IConnectionStore store, IDispatcher dispatcher, IQueryExecutor q, IClock clock)
    : IRequestHandler<TestConnectionQuery, TestResultDto>
{
    public async Task<Result<TestResultDto>> Handle(TestConnectionQuery r, CancellationToken ct)
    {
        var connection = await q.FirstOrDefaultAsync(connections.Query().Where(x => x.Id == r.Id), ct);
        if (connection is null) return Error.NotFound("Connection not found.");
        if (registry.Find(connection.Connector) is not { } connector) return Error.NotFound("This connector is no longer available.");
        // A connector that only stores its settings has nothing to test: refuse before anything is claimed, called or recorded.
        if (!connector.Descriptor.CanTest) return Error.Conflict("Testing is not available for this connector yet.");

        var now = clock.UtcNow;
        if (!await store.TryClaimTestAsync(connection.Id, now, ConnectionRules.TestInterval, ct))
        {
            var wait = connection.TestClaimedAt is { } last ? (int)Math.Ceiling((ConnectionRules.TestInterval - (now - last)).TotalSeconds) : (int)ConnectionRules.TestInterval.TotalSeconds;
            wait = Math.Clamp(wait, 1, (int)ConnectionRules.TestInterval.TotalSeconds);
            return Error.TooManyRequests($"This connection was tested a moment ago. Wait {wait} second{(wait == 1 ? "" : "s")} before testing it again.", wait);
        }

        var watch = Stopwatch.StartNew();
        ConnectorTestResult result;
        try { result = await connector.TestAsync(connection, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A connector should report problems as steps. If one throws, say so without its message (which could mention anything).
            result = new ConnectorTestResult(false, [new ConnectorStep("Test", false, "The test could not be completed.", (int)watch.ElapsedMilliseconds)]);
        }
        var durationMs = (int)watch.ElapsedMilliseconds;
        var steps = result.Steps.Select(s => new TestStepDto(s.Name, s.Ok, s.Detail, s.DurationMs)).ToList();

        var recorded = await dispatcher.Send(new RecordConnectionTestCommand(connection.Id, result.Ok, result.Steps.FirstOrDefault(s => !s.Ok)?.Detail, durationMs, now), ct);
        if (recorded.IsFailure) return recorded.Error;
        return new TestResultDto(result.Ok, durationMs, now, steps);
    }
}

/// <summary>Stores the outcome of a test on the connection and raises <c>integration.connection.tested</c>. Sent by <see cref="TestConnectionHandler"/> once the calls are done.</summary>
public sealed record RecordConnectionTestCommand(Guid Id, bool Ok, string? Detail, int DurationMs, DateTimeOffset At) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class RecordConnectionTestHandler(IRepository<IntegrationConnection> connections, IQueryExecutor q) : IRequestHandler<RecordConnectionTestCommand, Guid>
{
    public async Task<Result<Guid>> Handle(RecordConnectionTestCommand c, CancellationToken ct)
    {
        var connection = await q.FirstOrDefaultAsync(connections.Query().Where(x => x.Id == c.Id), ct);
        if (connection is null) return Error.NotFound("Connection not found.");   // deleted while the test ran
        connection.RecordTest(c.Ok, c.Detail, c.DurationMs, c.At);
        return connection.Id;
    }
}

// -------------------------------------------------------------------------------------------------------------------------------- activity

/// <summary>The calls made for a connection, newest first, paged by the call's id (ADR-0040 decision 5).</summary>
public sealed record ListConnectionActivityQuery(Guid Id, string? Cursor, int? Limit) : IQuery<Page<ActivityDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.IntegrationsManage;
}

public sealed class ListConnectionActivityHandler(IRepository<IntegrationConnection> connections, IRepository<IntegrationCall> calls, IQueryExecutor q)
    : IRequestHandler<ListConnectionActivityQuery, Page<ActivityDto>>
{
    public async Task<Result<Page<ActivityDto>>> Handle(ListConnectionActivityQuery r, CancellationToken ct)
    {
        if (!await q.AnyAsync(connections.Query().Where(x => x.Id == r.Id), ct)) return Error.NotFound("Connection not found.");
        var limit = Paging.Clamp(r.Limit);
        IQueryable<IntegrationCall> query = calls.Query().Where(x => x.ConnectionId == r.Id);
        if (Paging.ParseCursor(r.Cursor) is { } after) query = query.Where(x => x.Id.CompareTo(after) < 0);
        var rows = await q.ToListAsync(query.OrderByDescending(x => x.Id).Take(limit + 1), ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new Page<ActivityDto>(rows.Select(IntegrationMapping.ToDto).ToList(), more ? rows[^1].Id.ToString() : null);
    }
}
