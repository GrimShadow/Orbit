using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Common;
using Dam.Domain.Integrations;
using Dam.Infrastructure.Notifications;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Integrations;

/// <summary>The outcome of one call to another system. <see cref="Error"/> is sanitised and never contains a secret, a token or a response body.</summary>
public sealed record CallResult(bool Ok, int? StatusCode, string? Error, int DurationMs);

/// <summary>
/// Makes the calls a connector makes to another system (ADR-0040 decisions 4 and 5): authenticates by the connection's strategy, keeps the fetched token (encrypted on the
/// connection) and reuses it until a minute before it ends, signs in again at most once when a call is answered 401, lets only one caller sign in at a time, and records every
/// call in <c>integration_calls</c> (host and path only).
/// <para>
/// <b>No database transaction is held around a call.</b> Every statement here commits by itself, and the "one caller signs in at a time" rule is a lease on the connection's row
/// (a conditional UPDATE that expires), not a lock: nothing is held open across the HTTP request, so the audit-chain lock (ADR-0038) and the connection pool are never tied up by
/// a slow third party. Calling this inside a transaction is refused.
/// </para>
/// </summary>
public sealed class ConnectionRuntime(
    DamDbContext db, ISecretProtector protector, OutboundHttp http, IntegrationSettings settings, IClock clock, ITenantContext tenant, ILogger<ConnectionRuntime> log)
{
    private const string UserAgent = "Orbit-Integrations/1.0";

    private sealed class CallContext(IntegrationConnection connection, HttpAuthSettings authSettings, Dictionary<string, string> secrets)
    {
        public IntegrationConnection Connection => connection;
        public HttpAuthSettings Settings => authSettings;
        public string? Secret(string key) => secrets.GetValueOrDefault(key);
        /// <summary>Everything that must never appear in a message: the secrets, the tokens seen, and forms they take in headers.</summary>
        public HashSet<string> Redact { get; } = [.. secrets.Values];
    }

    private sealed record TokenOutcome(bool Ok, string Detail, string? Token, int DurationMs);
    private sealed record CacheRow(string? Cipher, DateTimeOffset? ExpiresAt);

    // ------------------------------------------------------------------------------------------------------------------- test step

    /// <summary>
    /// The "Authenticate" step of a test: for a stored credential, checks it can be read and is complete; for a token strategy, signs in (<paramref name="fresh"/>: even when a usable token is saved,
    /// so the test proves the credentials as they are now) and saves the token.
    /// </summary>
    public async Task<ConnectorStep> AuthenticateAsync(IntegrationConnection connection, bool fresh, CancellationToken ct)
    {
        RequireNoTransaction();
        var watch = Stopwatch.StartNew();
        var (ctx, error) = Prepare(connection);
        if (ctx is null) return new ConnectorStep("Authenticate", false, error!, Ms(watch));
        var s = ctx.Settings;
        if (!s.IsTokenBased)
        {
            var (addition, problem) = HttpAuth.ForStatic(s, ctx.Secret);
            if (addition is null) return new ConnectorStep("Authenticate", false, Clean(ctx, problem), Ms(watch));
            return new ConnectorStep("Authenticate", true,
                s.Strategy == AuthStrategies.None ? "No authentication is configured." : $"Uses {AuthStrategies.Label(s.Strategy)}; the credentials are sent with each request.", Ms(watch));
        }
        var token = await EnsureTokenAsync(ctx, fresh, rejected: null, ct);
        return new ConnectorStep("Authenticate", token.Ok, token.Detail, Ms(watch));
    }

    // ---------------------------------------------------------------------------------------------------------------------- a call

    /// <summary>
    /// Sends one authenticated request to a path on the connection's base address and records it. A token strategy fetches (or reuses) its token first; a 401 answer triggers
    /// exactly one new sign-in and one retry, never a loop. Does not check <see cref="IntegrationConnection.Enabled"/>: whether a disabled connection may be used is the caller's decision.
    /// </summary>
    /// <param name="kind">A <see cref="CallKinds"/> value, for the activity log.</param>
    public async Task<CallResult> SendAsync(IntegrationConnection connection, string kind, HttpMethod method, string path, CancellationToken ct)
    {
        // next step: the content-library actions of a connector call this to push an asset (they will add a body parameter and an idempotency rule, since a retry after 401 repeats the request).
        RequireNoTransaction();
        var watch = Stopwatch.StartNew();
        var (ctx, error) = Prepare(connection);
        if (ctx is null) return new CallResult(false, null, error, Ms(watch));
        var url = ConnectorUrls.Resolve(ctx.Settings.BaseUrl, path);
        if (url is null) return new CallResult(false, null, "The request path or the base URL is not valid.", Ms(watch));

        string? token = null;
        if (ctx.Settings.IsTokenBased)
        {
            var auth = await EnsureTokenAsync(ctx, fresh: false, rejected: null, ct);
            if (!auth.Ok) return new CallResult(false, null, auth.Detail, Ms(watch));
            token = auth.Token;
        }

        var first = await SendOnceAsync(ctx, kind, method, url, token, ct);
        if (first.StatusCode != 401) return first with { DurationMs = Ms(watch) };

        if (!ctx.Settings.IsTokenBased)
            return new CallResult(false, 401, "The server rejected the credentials (401). Check them.", Ms(watch));

        // The saved token may have been revoked: sign in once more (unless another caller already did) and retry once.
        var again = await EnsureTokenAsync(ctx, fresh: false, rejected: token, ct);
        if (!again.Ok) return new CallResult(false, 401, again.Detail, Ms(watch));
        var second = await SendOnceAsync(ctx, kind, method, url, again.Token, ct);
        return second.StatusCode == 401
            ? new CallResult(false, 401, "The server still rejected the token (401) after signing in again. Check the credentials and the token header.", Ms(watch))
            : second with { DurationMs = Ms(watch) };
    }

    private async Task<CallResult> SendOnceAsync(CallContext ctx, string kind, HttpMethod method, Uri url, string? token, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        AuthAddition addition;
        if (token is not null) addition = HttpAuth.ForToken(ctx.Settings, token);
        else
        {
            var (a, problem) = HttpAuth.ForStatic(ctx.Settings, ctx.Secret);
            if (a is null) return new CallResult(false, null, Clean(ctx, problem), Ms(watch));
            addition = a;
        }

        int? status = null;
        string? error = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using var request = new HttpRequestMessage(method, ConnectorUrls.WithQuery(url, addition.Query));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            foreach (var (name, value) in addition.Headers)
                if (!request.Headers.TryAddWithoutValidation(name, value)) throw new FormatException("A credential header could not be added.");
            using var response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) error = StatusMessage(status.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = ex is FormatException ? "A credential header could not be added; check the header name and the credential." : OutboundHttp.Explain(ex, timeout.IsCancellationRequested);
        }
        error = error is null ? null : Clean(ctx, error);
        var ms = Ms(watch);
        await RecordAsync(ctx.Connection.Id, kind, method.Method, url, status, error is null, ms, error);
        return new CallResult(error is null, status, error, ms);
    }

    private static string StatusMessage(int status) => status is >= 300 and < 400
        ? $"The server answered {status}. Redirects are not followed; use the final address as the base URL."
        : $"The server answered {status}.";

    // --------------------------------------------------------------------------------------------------------------------- tokens

    /// <summary>
    /// A usable token: the saved one while it is more than a minute from its end, else a new sign-in. Only one caller signs in at a time (a lease on the connection's row); the others
    /// wait for it and use its token. <paramref name="rejected"/> is a token the server just refused: a saved token equal to it is not reused.
    /// </summary>
    private async Task<TokenOutcome> EnsureTokenAsync(CallContext ctx, bool fresh, string? rejected, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var id = ctx.Connection.Id;
        var giveUp = settings.Timeout + TimeSpan.FromSeconds(20);
        while (true)
        {
            var cache = await ReadCacheAsync(id, ct);
            if (cache is null) return new TokenOutcome(false, "The connection no longer exists.", null, Ms(watch));
            if (!fresh && UsableToken(ctx, cache, rejected) is { } saved)
                return new TokenOutcome(true, $"Using the saved sign-in (valid for about {Minutes((cache.ExpiresAt!.Value - clock.UtcNow).TotalSeconds)} more).", saved, Ms(watch));
            if (await ClaimLeaseAsync(id, ct)) break;
            if (watch.Elapsed > giveUp) return new TokenOutcome(false, "Another sign-in for this connection is taking too long. Try again in a moment.", null, Ms(watch));
            await Task.Delay(settings.LeasePollInterval, ct);
        }

        try
        {
            // Another caller may have finished between our look and our claim.
            if (!fresh && await ReadCacheAsync(id, ct) is { } latest && UsableToken(ctx, latest, rejected) is { } saved)
                return new TokenOutcome(true, $"Using the saved sign-in (valid for about {Minutes((latest.ExpiresAt!.Value - clock.UtcNow).TotalSeconds)} more).", saved, Ms(watch));
            return await LoginAsync(ctx, watch, ct);
        }
        finally
        {
            await ReleaseLeaseAsync(id);   // idempotent: a successful sign-in has already cleared it in the same statement that saved the token
        }
    }

    private string? UsableToken(CallContext ctx, CacheRow cache, string? rejected)
    {
        if (cache.Cipher is null || !TokenLifetime.IsUsable(cache.ExpiresAt, clock.UtcNow)) return null;
        try
        {
            var token = protector.Unprotect(cache.Cipher, IntegrationSecretPurposes.Token(ctx.Connection.Id));
            if (rejected is not null && token == rejected) return null;
            ctx.Redact.Add(token);
            return token;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
        {
            return null;   // unreadable (the key changed): sign in again
        }
    }

    private async Task<TokenOutcome> LoginAsync(CallContext ctx, Stopwatch total, CancellationToken ct)
    {
        var (request, buildError) = HttpAuth.BuildTokenRequest(ctx.Settings, ctx.Secret);
        if (request is null) return new TokenOutcome(false, "Sign-in failed: " + Clean(ctx, buildError), null, Ms(total));

        var watch = Stopwatch.StartNew();
        int? status = null;
        string? error = null;
        TokenResponse? parsed = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            message.Headers.UserAgent.ParseAdd(UserAgent);
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (request.Body is not null) message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType!);
            using var response = await http.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) error = StatusMessage(status.Value);
            else
            {
                var body = await OutboundHttp.ReadExcerptAsync(response.Content, settings.MaxLoginResponseBytes, timeout.Token);
                parsed = HttpAuth.ReadTokenResponse(ctx.Settings, body);
                if (!parsed.Ok) error = parsed.Error;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = OutboundHttp.Explain(ex, timeout.IsCancellationRequested);
        }
        if (parsed?.Token is { } t) ctx.Redact.Add(t);
        error = error is null ? null : Clean(ctx, error);
        await RecordAsync(ctx.Connection.Id, CallKinds.Auth, request.Method, request.Url, status, error is null, Ms(watch), error);
        if (error is not null || parsed?.Token is not { } token) return new TokenOutcome(false, "Sign-in failed: " + (error ?? "no token."), null, Ms(total));

        var expiresAt = TokenLifetime.ExpiresAt(clock.UtcNow, parsed.LifetimeSeconds);
        await StoreTokenAsync(ctx.Connection.Id, token, expiresAt);
        return new TokenOutcome(true, $"Signed in; the token is valid for about {Minutes(parsed.LifetimeSeconds)}.", token, Ms(total));
    }

    private static string Minutes(double seconds) => seconds < 90
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(seconds))} seconds")
        : seconds < 5400
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(seconds / 60)} minutes")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(seconds / 3600, 1)} hours");

    // ----------------------------------------------------------------------------------------------------------------------- storage

    private async Task<CacheRow?> ReadCacheAsync(Guid id, CancellationToken ct) =>
        await db.IntegrationConnections.AsNoTracking().Where(c => c.Id == id).Select(c => new CacheRow(c.TokenCipher, c.TokenExpiresAt)).FirstOrDefaultAsync(ct);

    /// <summary>Takes the right to sign in, for the length of one sign-in and a margin. One conditional UPDATE, evaluated on the database's clock so API instances agree.</summary>
    private async Task<bool> ClaimLeaseAsync(Guid id, CancellationToken ct)
    {
        var seconds = (settings.Timeout + TimeSpan.FromSeconds(15)).TotalSeconds;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE integration_connections SET login_lease_until = now() + make_interval(secs => {seconds})
            WHERE id = {id} AND tenant_id = {tenant.TenantId} AND (login_lease_until IS NULL OR login_lease_until <= now())
            """, ct) == 1;
    }

    private async Task ReleaseLeaseAsync(Guid id)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE integration_connections SET login_lease_until = NULL WHERE id = {id} AND tenant_id = {tenant.TenantId}", CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or InvalidOperationException)
        {
            log.LogWarning("Could not release the sign-in lease of connection {Connection} ({Reason}); it expires by itself", id, ex.GetType().Name);
        }
    }

    private async Task StoreTokenAsync(Guid id, string token, DateTimeOffset expiresAt)
    {
        var cipher = protector.Protect(token, IntegrationSecretPurposes.Token(id));
        var end = expiresAt.ToUniversalTime();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE integration_connections SET token_cipher = {cipher}, token_expires_at = {end}, login_lease_until = NULL
            WHERE id = {id} AND tenant_id = {tenant.TenantId}
            """, CancellationToken.None);
    }

    /// <summary>Records one call: who, what kind, method, host and path (never the query), status, duration and a sanitised error. Failing to record never fails the call.</summary>
    private async Task RecordAsync(Guid connectionId, string kind, string method, Uri url, int? status, bool ok, int durationMs, string? error)
    {
        try
        {
            var id = Uuid7.NewGuid();
            var target = ConnectorUrls.Target(url);
            var at = clock.UtcNow.ToUniversalTime();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration_calls (id, tenant_id, connection_id, kind, method, target, status_code, ok, duration_ms, error, created_at)
                VALUES ({id}, {tenant.TenantId}, {connectionId}, {kind}, {method}, {target}, {status}, {ok}, {durationMs}, {error}, {at})
                """, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or InvalidOperationException)
        {
            log.LogWarning("Could not record a call of connection {Connection} ({Reason})", connectionId, ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------ helpers

    private (CallContext? Context, string? Error) Prepare(IntegrationConnection connection)
    {
        var secrets = new Dictionary<string, string>();
        try
        {
            foreach (var (key, stored) in connection.Secrets) secrets[key] = protector.Unprotect(stored.Cipher, IntegrationSecretPurposes.Field(connection.Id, key));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
        {
            return (null, "The stored credentials cannot be read. Check that DAM_SECRETS_KEY has not changed, or enter the credentials again.");
        }
        var ctx = new CallContext(connection, HttpAuthSettings.Read(connection.Config), secrets);
        if (ctx.Settings.Username is { Length: > 0 } user && secrets.TryGetValue(FieldKeys.Password, out var password))
            ctx.Redact.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        return (ctx, null);
    }

    private static string Clean(CallContext ctx, string? message) => ErrorSanitizer.Clean(message, ctx.Redact);

    private static int Ms(Stopwatch watch) => (int)watch.ElapsedMilliseconds;

    private void RequireNoTransaction()
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Connector calls must not run inside a database transaction (ADR-0040, ADR-0038).");
    }
}
