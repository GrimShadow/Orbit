using System.Diagnostics;
using System.Net;
using Dam.Domain.Assets;
using Dam.Infrastructure.Delivery;
using Dam.UnitTests.Support;

namespace Dam.IntegrationTests;

/// <summary>
/// ADR-0038: a rendition job must not hold the tenant's audit-chain lock while ffmpeg, LibreOffice or poppler work. The audit trigger takes a per-tenant
/// advisory lock on the first audit row a transaction writes and keeps it until the transaction ends; the consumer host used to wrap the whole rendition
/// job in one transaction, so every audited write for that tenant (any API command) waited for the longest transcode.
/// </summary>
[Collection("pg")]
public sealed class RenditionLockTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly TimeSpan SlowTranscode = TimeSpan.FromSeconds(8);

    /// <summary>An ffmpeg that is slow on the first video transcode only (it says so first, by creating the marker), so a test can act while the job is mid-work.</summary>
    private static (string Script, string Marker) SlowFfmpeg()
    {
        var dir = Directory.CreateTempSubdirectory("orbit-slow-ffmpeg-").FullName;
        var marker = Path.Combine(dir, "transcoding");
        var script = Path.Combine(dir, "ffmpeg");
        File.WriteAllText(script, $"""
            #!/bin/sh
            for a in "$@"; do
              if [ "$a" = "libx264" ] && [ ! -e '{marker}' ]; then touch '{marker}'; sleep {(int)SlowTranscode.TotalSeconds}; fi
            done
            exec ffmpeg "$@"
            """.Replace("\r\n", "\n"));
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The slow ffmpeg is a shell script.");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, marker);
    }

    private async Task<IReadOnlyList<string>> ProfilesOf(string admin, Guid asset, int expected, int seconds)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            var list = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/renditions", admin)).Body;
            if (list.GetArrayLength() >= expected) return list.EnumerateArray().Select(r => r.GetProperty("profile").GetString()!).ToList();
            await Task.Delay(200);
        }
        throw new TimeoutException("Renditions did not finish generating.");
    }

    [Fact]
    public async Task An_audited_write_for_the_tenant_does_not_wait_for_a_video_transcode_in_progress()
    {
        var (script, marker) = SlowFfmpeg();
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(new Dictionary<string, string?>
        { ["DAM_SCAN_MODE"] = "off", ["DAM_FFPROBE_PATH"] = "ffprobe", ["DAM_FFMPEG_PATH"] = script });
        await worker.StartAsync();

        var asset = await NewAsset(admin, folder, type: "video");
        var upload = await Ingest(admin, asset, TestImages.Video(2, 320, 240), "clip.mp4", "video/mp4");
        Assert.Equal("ingested", upload.GetProperty("status").GetString());

        // The poster stills are stored (and audited) first; then the first transcode starts and takes its time.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(File.Exists(marker), "The transcode never started.");

        var clock = Stopwatch.StartNew();
        var created = await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Created while a video transcodes" });
        clock.Stop();

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"An audited write waited {clock.Elapsed.TotalSeconds:0.0} s for the rendition job (the transcode takes {SlowTranscode.TotalSeconds:0} s).");

        // The job itself still finishes everything.
        var expected = RenditionProfiles.VideoStills.Count + RenditionProfiles.Videos.Count;
        var done = await ProfilesOf(admin, asset, expected, seconds: 90);
        Assert.Equal(expected, done.Distinct().Count());
    }
}
