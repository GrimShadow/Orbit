using System.Net;
using System.Net.Sockets;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Webhooks;

namespace Dam.Infrastructure.Notifications;

public sealed class BlockedDestinationException(string message) : Exception(message);

/// <summary>
/// The one HTTP client used to call out to webhook receivers and chat services (ADR-0031). It never follows redirects (a redirect could
/// lead somewhere the URL check never saw), ignores system proxies, and connects only to addresses it has itself checked: the connect
/// callback resolves the host, refuses any private, loopback or link-local answer (unless <see cref="WebhookSettings.AllowPrivate"/>), and
/// dials the very IP it validated, so a DNS answer that changes between the check and the connection (rebinding) gains nothing.
/// </summary>
public sealed class OutboundHttp : IDisposable
{
    private readonly WebhookSettings _settings;
    private readonly SocketsHttpHandler _handler;

    public OutboundHttp(WebhookSettings settings)
    {
        _settings = settings;
        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = settings.Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = ConnectAsync,
        };
        Client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public HttpClient Client { get; }

    /// <summary>Resolves a host to the addresses it may be called on. Throws <see cref="BlockedDestinationException"/> when any is not allowed.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0) throw new BlockedDestinationException("The host name does not resolve.");
        if (!_settings.AllowPrivate && addresses.Any(AddressGuard.IsBlocked))
            throw new BlockedDestinationException("The address is private, internal or otherwise not allowed.");
        return addresses;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolveAsync(context.DnsEndPoint.Host, ct);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The first <paramref name="maxBytes"/> of a response body as text, without reading further or throwing on odd bytes.</summary>
    public static async Task<string> ReadExcerptAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var read = 0;
        while (read < maxBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, maxBytes - read), ct);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read).Replace("\0", "", StringComparison.Ordinal);
    }

    /// <summary>A short explanation of a failed call, without leaking stack traces.</summary>
    public static string Explain(Exception ex, bool timedOut)
    {
        if (timedOut) return "The request timed out.";
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is BlockedDestinationException b) return $"Blocked: {b.Message}";
            if (e is SocketException s) return $"Could not connect ({s.SocketErrorCode}).";
        }
        return ex is HttpRequestException h ? $"Request failed: {h.Message}" : $"Request failed: {ex.GetType().Name}";
    }

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
    }
}

/// <summary>Checks a URL when it is saved: syntax, then (unless private targets are allowed) that every address it resolves to is public.</summary>
public sealed class OutboundUrlGuard(OutboundHttp http, WebhookSettings settings) : IOutboundUrlGuard
{
    public async Task<string?> CheckAsync(string url, CancellationToken ct)
    {
        if (AddressGuard.CheckSyntax(url) is { } syntax) return syntax;
        if (settings.AllowPrivate) return null;
        var uri = new Uri(url.Trim());
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await http.ResolveAsync(uri.Host, timeout.Token);
            return null;
        }
        catch (BlockedDestinationException ex)
        {
            return $"This URL cannot be used: {ex.Message.TrimEnd('.')}.";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "The host name could not be resolved.";
        }
    }
}
