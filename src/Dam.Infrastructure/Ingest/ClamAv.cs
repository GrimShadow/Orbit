using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Dam.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure.Ingest;

public sealed class ScanSettings
{
    /// <summary>"clamav" scans with clamd; "off" skips scanning (development only) and records that it was skipped.</summary>
    public string Mode { get; set; } = "clamav";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    /// <summary>clamd will not scan streams past its configured limit, so larger files are skipped and recorded.</summary>
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>What to do with a file too large to scan: "allow" (record it) or "block" (refuse it).</summary>
    public string Oversize { get; set; } = "allow";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Speaks clamd's INSTREAM protocol: the command, then the content in length-prefixed chunks, then a zero-length chunk;
/// clamd answers "stream: OK", "stream: &lt;signature&gt; FOUND" or an error. Works on any duplex stream, so it is unit-testable.
/// </summary>
public static class ClamAvProtocol
{
    private const int ChunkBytes = 1024 * 1024;

    public static async Task<ScanOutcome> ScanAsync(Stream clamd, Stream content, CancellationToken ct)
    {
        await clamd.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);
        var buffer = new byte[ChunkBytes];
        var length = new byte[4];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)read);
            await clamd.WriteAsync(length, ct);
            await clamd.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, 0);
        await clamd.WriteAsync(length, ct);
        await clamd.FlushAsync(ct);

        var reply = await ReadReplyAsync(clamd, ct);
        return Parse(reply);
    }

    private static async Task<string> ReadReplyAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (await s.ReadAsync(one, ct) == 1)
        {
            if (one[0] == 0 || one[0] == (byte)'\n') break;
            sb.Append((char)one[0]);
            if (sb.Length > 4096) break;
        }
        return sb.ToString().Trim();
    }

    public static ScanOutcome Parse(string reply)
    {
        // "stream: OK"  |  "stream: Eicar-Test-Signature FOUND"  |  "INSTREAM size limit exceeded. ERROR"
        if (reply.EndsWith("OK", StringComparison.Ordinal)) return new ScanOutcome(ScanVerdict.Clean, "clamav");
        if (reply.EndsWith("FOUND", StringComparison.Ordinal))
        {
            var body = reply.StartsWith("stream:", StringComparison.Ordinal) ? reply["stream:".Length..] : reply;
            return new ScanOutcome(ScanVerdict.Infected, "clamav", body[..^"FOUND".Length].Trim());
        }
        throw new InvalidOperationException($"clamd could not scan the file: {(reply.Length == 0 ? "no reply" : reply)}");
    }
}

public sealed class ClamAvScanner(ScanSettings settings) : IVirusScanner
{
    public async Task<ScanOutcome> ScanAsync(Stream content, long length, CancellationToken ct)
    {
        if (length > settings.MaxBytes)
            return new ScanOutcome(ScanVerdict.Skipped, "clamav", Note: $"oversize: {length} bytes exceeds the scan limit of {settings.MaxBytes} bytes");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(settings.Host, settings.Port, timeout.Token); // unreachable -> throws -> retried, then the upload fails (fail closed)
        await using var net = tcp.GetStream();
        return await ClamAvProtocol.ScanAsync(net, content, timeout.Token);
    }
}

/// <summary>Development only (DAM_SCAN_MODE=off): nothing is scanned, and every version records that it was not.</summary>
public sealed class DisabledScanner : IVirusScanner
{
    public Task<ScanOutcome> ScanAsync(Stream content, long length, CancellationToken ct) =>
        Task.FromResult(new ScanOutcome(ScanVerdict.Skipped, "none", Note: "scanning is disabled (DAM_SCAN_MODE=off)"));
}

public static class IngestRegistration
{
    /// <summary>Reads DAM_CLAMAV_HOST/PORT, DAM_SCAN_MODE, DAM_SCAN_MAX_MB, DAM_SCAN_OVERSIZE and DAM_FFPROBE_PATH.</summary>
    public static IServiceCollection AddDamIngest(this IServiceCollection services, IConfiguration cfg)
    {
        var scan = new ScanSettings
        {
            Mode = (cfg["DAM_SCAN_MODE"] ?? "clamav").ToLowerInvariant(),
            Host = cfg["DAM_CLAMAV_HOST"] ?? "localhost",
            Port = int.TryParse(cfg["DAM_CLAMAV_PORT"], out var port) ? port : 3310,
            Oversize = (cfg["DAM_SCAN_OVERSIZE"] ?? "allow").ToLowerInvariant(),
        };
        if (long.TryParse(cfg["DAM_SCAN_MAX_MB"], out var mb) && mb > 0) scan.MaxBytes = mb * 1024 * 1024;
        if (scan.Mode is not ("clamav" or "off")) throw new InvalidOperationException("DAM_SCAN_MODE must be 'clamav' or 'off'.");
        if (scan.Oversize is not ("allow" or "block")) throw new InvalidOperationException("DAM_SCAN_OVERSIZE must be 'allow' or 'block'.");

        services.AddSingleton(scan);
        if (scan.Mode == "off") services.AddSingleton<IVirusScanner, DisabledScanner>();
        else services.AddSingleton<IVirusScanner, ClamAvScanner>();
        services.AddSingleton<IMediaProbe>(_ => new FfprobeMediaProbe(cfg["DAM_FFPROBE_PATH"] ?? "ffprobe", _.GetRequiredService<IObjectStore>()));
        services.AddSingleton<TechnicalMetadataExtractor>();
        return services;
    }
}
