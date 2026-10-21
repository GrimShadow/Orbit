using Dam.Domain.Integrations;

namespace Dam.Application.Abstractions;

/// <param name="Name">What the step did (Authenticate, Test request).</param>
/// <param name="Detail">A sentence for the user. Never contains a secret, a token or a response body.</param>
public sealed record ConnectorStep(string Name, bool Ok, string Detail, int DurationMs);

public sealed record ConnectorTestResult(bool Ok, IReadOnlyList<ConnectorStep> Steps);

/// <summary>
/// A kind of external system the DAM can talk to (ADR-0040 decision 1). The implementation is registered in DI next to its descriptor; a connection is one tenant's configured use of it.
/// Implementations are scoped (they use the request's tenant and database) and must make their HTTP calls outside any database transaction.
/// </summary>
public interface IConnector
{
    ConnectorDescriptor Descriptor { get; }

    /// <summary>Signs in and, when the connection has a test path, makes one GET of it. Does not change the connection (the caller records the result).</summary>
    Task<ConnectorTestResult> TestAsync(IntegrationConnection connection, CancellationToken ct);

    // next step: actions. A connector step adds its actions here (an IConnectorAction shape and an integration_deliveries log with retries, modelled on webhook deliveries; ADR-0040 decision 7).
}

/// <summary>Database operations the connection use cases need that are not plain aggregate changes.</summary>
public interface IConnectionStore
{
    /// <summary>
    /// Reserves the right to test the connection now. False when a test started within <paramref name="interval"/>. One statement, so two requests (or two API instances)
    /// cannot both win.
    /// </summary>
    Task<bool> TryClaimTestAsync(Guid connectionId, DateTimeOffset now, TimeSpan interval, CancellationToken ct);
}
