using System.Net;
using System.Text;
using System.Text.Json;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Notifications;
using Dam.Shared.Webhooks;

namespace Dam.UnitTests;

/// <summary>The pure parts of webhooks (ADR-0031): signing, retry schedule, event patterns, the event catalogue, the SSRF address rules, secrets at rest.</summary>
public sealed class WebhookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private const string Secret = "whsec_test-secret-0123456789";
    private const string Body = """{"id":"x","type":"asset.published"}""";

    // ---- signature ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_signature_is_v1_and_hex_hmac_over_timestamp_dot_body()
    {
        var ts = Now.ToUnixTimeSeconds();
        var header = WebhookSignature.Sign(Secret, ts, Body);
        Assert.StartsWith("v1=", header);
        Assert.Equal(3 + 64, header.Length);
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var expected = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{Body}")));
        Assert.Equal("v1=" + expected, header);
    }

    [Fact]
    public void A_correct_delivery_verifies()
    {
        var ts = Now.ToUnixTimeSeconds();
        Assert.Equal(WebhookVerification.Valid,
            WebhookSignature.Verify(Secret, ts.ToString(), WebhookSignature.Sign(Secret, ts, Body), Body, Now, WebhookSignature.DefaultTolerance));
    }

    [Fact]
    public void A_changed_body_a_wrong_secret_or_a_changed_timestamp_is_a_bad_signature()
    {
        var ts = Now.ToUnixTimeSeconds();
        var sig = WebhookSignature.Sign(Secret, ts, Body);
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify(Secret, ts.ToString(), sig, Body + " ", Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify("whsec_other", ts.ToString(), sig, Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify(Secret, (ts + 1).ToString(), sig, Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify(Secret, ts.ToString(), "v1=" + new string('0', 64), Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify(Secret, ts.ToString(), "v1=nothex", Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify(Secret, ts.ToString(), "v2=" + sig[3..], Body, Now, WebhookSignature.DefaultTolerance));
    }

    [Theory]
    [InlineData(-299, true)]
    [InlineData(299, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    public void The_timestamp_must_be_within_the_tolerance_either_way_so_old_captures_cannot_be_replayed(int skewSeconds, bool accepted)
    {
        var ts = Now.AddSeconds(skewSeconds).ToUnixTimeSeconds();
        var result = WebhookSignature.Verify(Secret, ts.ToString(), WebhookSignature.Sign(Secret, ts, Body), Body, Now, TimeSpan.FromMinutes(5));
        Assert.Equal(accepted ? WebhookVerification.Valid : WebhookVerification.OutsideTolerance, result);
    }

    [Fact]
    public void A_wrong_secret_is_never_reported_as_a_stale_timestamp()
    {
        var old = Now.AddDays(-1).ToUnixTimeSeconds();
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify("whsec_other", old.ToString(), WebhookSignature.Sign(Secret, old, Body), Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Missing_or_malformed_headers_are_reported()
    {
        var ts = Now.ToUnixTimeSeconds();
        var sig = WebhookSignature.Sign(Secret, ts, Body);
        Assert.Equal(WebhookVerification.MissingHeaders, WebhookSignature.Verify(Secret, null, sig, Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.MissingHeaders, WebhookSignature.Verify(Secret, ts.ToString(), null, Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.MalformedTimestamp, WebhookSignature.Verify(Secret, "yesterday", sig, Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.MalformedTimestamp, WebhookSignature.Verify(Secret, "-5", sig, Body, Now, WebhookSignature.DefaultTolerance));
    }

    [Fact]
    public void A_header_listing_several_signatures_verifies_if_any_matches_which_allows_secret_rotation()
    {
        var ts = Now.ToUnixTimeSeconds();
        var header = WebhookSignature.Sign("whsec_old", ts, Body) + "," + WebhookSignature.Sign(Secret, ts, Body);
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(Secret, ts.ToString(), header, Body, Now, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(Secret, ts.ToString(), header.Replace(",", " "), Body, Now, WebhookSignature.DefaultTolerance));
    }

    [Fact]
    public void Generated_secrets_have_the_prefix_32_random_bytes_and_never_repeat()
    {
        var a = WebhookSignature.GenerateSecret();
        var b = WebhookSignature.GenerateSecret();
        Assert.NotEqual(a, b);
        Assert.StartsWith("whsec_", a);
        Assert.Equal(32, Convert.FromBase64String(a["whsec_".Length..].Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.DoesNotContain('=', a);
    }

    // ---- retry schedule ----------------------------------------------------------------------------------------

    [Fact]
    public void The_default_schedule_is_ten_seconds_then_a_minute_five_minutes_half_an_hour_two_six_and_twelve_hours_over_eight_attempts()
    {
        var policy = WebhookRetryPolicy.Default;
        Assert.Equal(8, policy.MaxAttempts);
        var waits = Enumerable.Range(1, 7).Select(n => policy.DelayAfter(n, () => 0.5)).ToArray();   // 0.5 = no jitter
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12)], waits);
        Assert.Null(policy.DelayAfter(8, () => 0.5));   // the eighth failure is final
        Assert.Null(policy.DelayAfter(9, () => 0.5));
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.999999, 1.2)]
    public void Jitter_scales_each_wait_within_plus_or_minus_twenty_percent(double random, double factor)
    {
        var wait = WebhookRetryPolicy.Default.DelayAfter(3, () => random)!.Value;
        Assert.Equal(300 * factor, wait.TotalSeconds, 0.01);
    }

    [Fact]
    public void A_policy_with_fewer_attempts_ends_sooner_and_one_with_more_steps_than_delays_repeats_the_last()
    {
        var two = WebhookRetryPolicy.Default with { MaxAttempts = 2 };
        Assert.NotNull(two.DelayAfter(1, () => 0.5));
        Assert.Null(two.DelayAfter(2, () => 0.5));
        var many = WebhookRetryPolicy.Default with { MaxAttempts = 12 };
        Assert.Equal(TimeSpan.FromHours(12), many.DelayAfter(11, () => 0.5));
    }

    // ---- event patterns ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("*", "asset.published", true)]
    [InlineData("asset.*", "asset.published", true)]
    [InlineData("asset.*", "asset.status.changed", true)]
    [InlineData("asset.*", "asset", false)]
    [InlineData("asset.*", "assets.created", false)]
    [InlineData("workflow.*", "workflow.sla.breached", true)]
    [InlineData("workflow.task.*", "workflow.task.created", true)]
    [InlineData("workflow.task.*", "workflow.sla.breached", false)]
    [InlineData("asset.published", "asset.published", true)]
    [InlineData("asset.published", "asset.published.v2", false)]
    [InlineData("asset.published", "asset.archived", false)]
    public void Patterns_match_exact_names_prefix_wildcards_and_star(string pattern, string name, bool expected) =>
        Assert.Equal(expected, EventPattern.Matches(pattern, name));

    [Theory]
    [InlineData("*", true)]
    [InlineData("asset.*", true)]
    [InlineData("asset.published", true)]
    [InlineData("workflow.task.created", true)]
    [InlineData("rendition.ready", true)]
    [InlineData("", false)]
    [InlineData("asset.", false)]
    [InlineData("*.published", false)]
    [InlineData("asset.*.created", false)]
    [InlineData("Asset.Published", false)]
    [InlineData("asset published", false)]
    [InlineData("asset.**", false)]
    public void Only_well_formed_patterns_are_valid(string pattern, bool valid) => Assert.Equal(valid, EventPattern.IsValid(pattern));

    [Fact]
    public void Normalize_trims_lowercases_and_removes_duplicates()
    {
        Assert.Equal(["asset.*", "workflow.task.created"], EventPattern.Normalize([" Asset.* ", "asset.*", "", "WORKFLOW.task.created"]));
        Assert.True(EventPattern.MatchesAny(["collection.updated", "asset.*"], "asset.created"));
        Assert.False(EventPattern.MatchesAny([], "asset.created"));
    }

    // ---- catalogue ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_catalogue_has_the_events_the_spec_names_and_every_source_event_is_listed_once()
    {
        foreach (var name in new[] { "asset.created", "asset.published", "asset.expired", "asset.archived", "asset.deleted", "asset.version.added", "rendition.ready",
                     "workflow.task.created", "workflow.task.completed", "workflow.sla.breached", "collection.updated", "comment.added", "comment.resolved", "comment.reopened", "webhook.ping" })
            Assert.True(EventCatalogue.IsKnown(name), name);
        Assert.Equal(EventCatalogue.All.Count, EventCatalogue.All.Select(e => e.Name).Distinct().Count());
        Assert.DoesNotContain("asset.published", EventCatalogue.SourceEvents);   // derived
        Assert.DoesNotContain("webhook.ping", EventCatalogue.SourceEvents);      // synthetic
        Assert.Contains("asset.status.changed", EventCatalogue.SourceEvents);
        Assert.False(EventCatalogue.IsKnown("upload.completed"));                // internal events are never public
        Assert.False(EventCatalogue.IsKnown("comment.edited"));                  // audit and notification only
        Assert.False(EventCatalogue.IsKnown("comment.deleted"));
        Assert.All(new[] { "comment.added", "comment.resolved", "comment.reopened" }, n => Assert.Contains(n, EventCatalogue.SourceEvents));
    }

    private static string Payload(Guid id, string to = "published") =>
        JsonSerializer.Serialize(new { EventId = id, OccurredAt = Now, AssetId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1"), From = "approved", To = to });

    [Fact]
    public void An_event_expands_to_itself_with_camel_case_data_and_the_envelope_fields_hoisted()
    {
        var id = Guid.NewGuid();
        var events = EventCatalogue.Expand("asset.metadata.updated", Payload(id), Guid.NewGuid(), DateTimeOffset.MinValue);
        var e = Assert.Single(events);
        Assert.Equal((id, "asset.metadata.updated", Now), (e.Id, e.Type, e.OccurredAt));
        Assert.True(e.Data.TryGetProperty("assetId", out _));
        Assert.False(e.Data.TryGetProperty("AssetId", out _));
        Assert.False(e.Data.TryGetProperty("eventId", out _));
        Assert.False(e.Data.TryGetProperty("occurredAt", out _));
    }

    [Theory]
    [InlineData("published")]
    [InlineData("expired")]
    [InlineData("archived")]
    [InlineData("deleted")]
    public void A_status_change_to_a_spec_status_also_produces_the_derived_event_with_a_stable_distinct_id(string to)
    {
        var id = Guid.NewGuid();
        var first = EventCatalogue.Expand("asset.status.changed", Payload(id, to), Guid.NewGuid(), Now);
        var again = EventCatalogue.Expand("asset.status.changed", Payload(id, to), Guid.NewGuid(), Now);
        Assert.Equal(["asset.status.changed", "asset." + to], first.Select(e => e.Type));
        Assert.NotEqual(first[0].Id, first[1].Id);
        Assert.Equal(first[1].Id, again[1].Id);   // a redelivery derives the same id, so it de-duplicates
        Assert.Equal("approved", first[1].Data.GetProperty("from").GetString());
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("in_review")]
    [InlineData("approved")]
    public void Other_status_changes_produce_only_the_original_event(string to) =>
        Assert.Single(EventCatalogue.Expand("asset.status.changed", Payload(Guid.NewGuid(), to), Guid.NewGuid(), Now));

    [Fact]
    public void Internal_and_synthetic_events_expand_to_nothing()
    {
        Assert.Empty(EventCatalogue.Expand("upload.completed", "{}", Guid.NewGuid(), Now));
        Assert.Empty(EventCatalogue.Expand("webhook.ping", "{}", Guid.NewGuid(), Now));
        Assert.Empty(EventCatalogue.Expand("role.created", "{}", Guid.NewGuid(), Now));
    }

    [Fact]
    public void A_payload_without_an_event_id_falls_back_to_the_message_id()
    {
        var fallback = Guid.NewGuid();
        Assert.Equal(fallback, Assert.Single(EventCatalogue.Expand("collection.updated", """{"CollectionId":"0197a000-0000-7000-8000-0000000000c1"}""", fallback, Now)).Id);
    }

    [Fact]
    public void The_envelope_has_id_type_tenant_time_and_data()
    {
        var id = Guid.NewGuid();
        var e = Assert.Single(EventCatalogue.Expand("asset.created", JsonSerializer.Serialize(new { EventId = id, OccurredAt = Now, AssetId = Guid.NewGuid(), AssetType = "image" }), id, Now));
        using var doc = JsonDocument.Parse(WebhookEnvelope.Build(e, Tenant));
        var root = doc.RootElement;
        Assert.Equal(["id", "type", "tenantId", "occurredAt", "data"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(id, root.GetProperty("id").GetGuid());
        Assert.Equal("asset.created", root.GetProperty("type").GetString());
        Assert.Equal(Tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(Now, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("image", root.GetProperty("data").GetProperty("assetType").GetString());
    }

    [Fact]
    public void A_webhook_wants_an_event_only_while_active_and_when_a_pattern_matches()
    {
        var hook = Webhook.Create(Tenant, "Hook", "https://example.com/h", ["asset.*"], true, "cipher", "abcd", null, Now);
        Assert.True(hook.Wants("asset.published"));
        Assert.False(hook.Wants("workflow.task.created"));
        hook.Update("Hook", "https://example.com/h", ["asset.*"], false, Now);
        Assert.False(hook.Wants("asset.published"));
    }

    // ---- SSRF address rules ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]    // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")]          // carrier-grade NAT
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]       // AWS IPv6 metadata
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")]    // IPv4-mapped
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("64:ff9b::7f00:1")]     // NAT64 of 127.0.0.1
    [InlineData("2002:7f00:1::1")]      // 6to4 of 127.0.0.1
    public void Private_internal_and_special_addresses_are_blocked(string address) => Assert.True(AddressGuard.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]      // just outside 172.16/12
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]      // just outside 100.64/10
    [InlineData("100.128.0.1")]
    [InlineData("169.253.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]    // NAT64 of 8.8.8.8
    public void Ordinary_public_addresses_are_allowed(string address) => Assert.False(AddressGuard.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("https://example.com/hook", null)]
    [InlineData("http://example.com:8080/a?b=c", null)]
    [InlineData("ftp://example.com/x", "Only http and https URLs are allowed.")]
    [InlineData("file:///etc/passwd", "Only http and https URLs are allowed.")]
    [InlineData("gopher://example.com", "Only http and https URLs are allowed.")]
    [InlineData("javascript:alert(1)", "Only http and https URLs are allowed.")]
    [InlineData("example.com/hook", "Enter a full URL such as https://example.com/hook.")]
    [InlineData("https://user:pass@example.com/x", "The URL must not contain a user name or password.")]
    [InlineData("", "A URL is required.")]
    [InlineData("   ", "A URL is required.")]
    public void URL_syntax_is_checked_without_touching_the_network(string url, string? problem) => Assert.Equal(problem, AddressGuard.CheckSyntax(url));

    [Fact]
    public void An_overlong_URL_is_refused() => Assert.NotNull(AddressGuard.CheckSyntax("https://example.com/" + new string('a', 2100)));

    // ---- secrets at rest ---------------------------------------------------------------------------------------

    [Fact]
    public void Secrets_round_trip_and_are_not_stored_in_the_clear_or_repeatably()
    {
        var protector = new AesGcmSecretProtector("unit-test-secrets-key-0123456789");
        var a = protector.Protect(Secret, "webhook-secret");
        var b = protector.Protect(Secret, "webhook-secret");
        Assert.NotEqual(a, b);                                   // a fresh nonce each time
        Assert.DoesNotContain(Secret, a);
        Assert.StartsWith("v1.", a);
        Assert.Equal(Secret, protector.Unprotect(a, "webhook-secret"));
        Assert.Equal(Secret, protector.Unprotect(b, "webhook-secret"));
    }

    [Fact]
    public void A_secret_cannot_be_read_for_another_purpose_with_another_key_or_after_tampering()
    {
        var protector = new AesGcmSecretProtector("unit-test-secrets-key-0123456789");
        var stored = protector.Protect(Secret, "webhook-secret");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(stored, "chat-url"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new AesGcmSecretProtector("another-key-another-key-another").Unprotect(stored, "webhook-secret"));
        var raw = Convert.FromBase64String(stored[3..]);
        raw[^1] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect("v1." + Convert.ToBase64String(raw), "webhook-secret"));
    }

    [Fact]
    public void Without_a_key_secrets_are_refused_rather_than_stored_in_the_clear_and_a_weak_key_is_rejected()
    {
        var none = new AesGcmSecretProtector(null);
        Assert.Contains("DAM_SECRETS_KEY", Assert.Throws<InvalidOperationException>(() => none.Protect(Secret, "webhook-secret")).Message);
        Assert.Throws<InvalidOperationException>(() => new AesGcmSecretProtector("short"));
    }
}
