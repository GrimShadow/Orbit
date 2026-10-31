using Dam.Application.Integrations;
using Dam.Application.Messaging;
using Dam.Shared.Identity;
using Dam.Shared.Integrations;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>Connections to other systems: the connector catalogue, connections with their secrets handled and never returned, tests and the activity log (integrations.manage). ADR-0040.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/integrations").WithTags("Integrations");

        g.MapGet("/connectors", (IDispatcher d, CancellationToken ct) => Run(d, new ListConnectorsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListConnectors")
            .WithSummary("The systems Orbit can connect to, with the fields each needs. Status is available, preview or planned (planned ones cannot be connected yet). Needs integrations.manage")
            .Produces<IReadOnlyList<ConnectorDto>>();

        var connections = g.MapGroup("/connections");
        connections.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListConnectionsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListConnections").WithSummary("The tenant's connections with their status. Secrets are shown only as set/not set and a hint. Needs integrations.manage")
            .Produces<IReadOnlyList<ConnectionDto>>();
        connections.MapPost("", (ConnectionInput r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateConnectionCommand(r), id => new GetConnectionQuery(id), ct, "/api/v1/integrations/connections"))
            .WithName("CreateConnection")
            .WithSummary("Creates a connection. Secrets go in 'secrets' and are stored encrypted. 422 names the fields as config.<field> or secrets.<field>; 409 when the name is taken; planned connectors are refused")
            .Produces<ConnectionDto>(StatusCodes.Status201Created);
        connections.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetConnectionQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetConnection").Produces<ConnectionDto>();
        connections.MapPut("/{id:guid}", (Guid id, ConnectionInput r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateConnectionCommand(id, r), cid => new GetConnectionQuery(cid), ct))
            .WithName("UpdateConnection")
            .WithSummary("Replaces a connection's name, enabled flag and settings. A secret you send replaces the stored one, one you leave out is kept, and 'clearSecrets' removes the named ones")
            .Produces<ConnectionDto>();
        connections.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new DeleteConnectionCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteConnection").WithSummary("Deletes a connection, its stored secrets and its activity log");
        connections.MapPost("/{id:guid}/test", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new TestConnectionQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("TestConnection")
            .WithSummary("Signs in to the other system and, when the connection has a test path, requests it once. Returns the steps and updates the connection's status. At most once every 5 seconds per connection (429 otherwise)")
            .Produces<TestResultDto>();
        connections.MapGet("/{id:guid}/activity", (Guid id, IDispatcher d, CancellationToken ct, string? cursor, int? limit) =>
                Run(d, new ListConnectionActivityQuery(id, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListConnectionActivity")
            .WithSummary("The calls made for a connection, newest first (host and path only, never a query string, body or header). The latest 500 are kept")
            .Produces<Page<ActivityDto>>();
    }
}
