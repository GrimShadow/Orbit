using Dam.Application.Abstractions;
using Dam.Domain.Integrations;

namespace Dam.Infrastructure.Integrations;

/// <summary>
/// The connector behind <c>http</c>: an HTTP API with a configurable authentication strategy (ADR-0040 decision 1). A specific connector that talks HTTP (what is pushed, in what shape)
/// is a subclass or a wrapper around this plus its actions, in its own step; PADS4 will be one (for now it is a stored-credentials connector that makes no calls).
/// </summary>
public class HttpApiConnector(ConnectorDescriptor descriptor, ConnectionRuntime runtime) : IConnector
{
    public ConnectorDescriptor Descriptor => descriptor;

    protected ConnectionRuntime Runtime => runtime;

    public async Task<ConnectorTestResult> TestAsync(IntegrationConnection connection, CancellationToken ct)
    {
        var steps = new List<ConnectorStep>();
        var authenticate = await runtime.AuthenticateAsync(connection, fresh: true, ct);
        steps.Add(authenticate);
        if (!authenticate.Ok) return new ConnectorTestResult(false, steps);

        var testPath = HttpAuthSettings.Read(connection.Config).TestPath;
        if (string.IsNullOrEmpty(testPath)) return new ConnectorTestResult(true, steps);

        var shown = testPath.Split('?', 2)[0];   // the query string may carry a key; it is never shown
        var call = await runtime.SendAsync(connection, CallKinds.Test, HttpMethod.Get, testPath, ct);
        steps.Add(new ConnectorStep("Test request", call.Ok,
            call.Ok ? $"GET {shown} answered {call.StatusCode}." : $"GET {shown} failed: {call.Error}", call.DurationMs));
        return new ConnectorTestResult(call.Ok, steps);
    }
}
