using System.Text.Json;
using Dam.Application.Audit;
using Dam.Application.Notifications;
using Dam.Domain.Notifications;
using Dam.Infrastructure.Notifications;
using Dam.Shared.Audit;

namespace Dam.UnitTests;

/// <summary>Notification rules, recipient planning, preference defaults, email templates and chat payloads (ADR-0031).</summary>
public sealed class NotificationTests
{
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid Asset = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Alice = Guid.Parse("0197a000-0000-7000-8000-0000000000aa");
    private static readonly Guid Bob = Guid.Parse("0197a000-0000-7000-8000-0000000000bb");
    private static readonly Guid Cara = Guid.Parse("0197a000-0000-7000-8000-0000000000cc");
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    // ---- categories and preference defaults -------------------------------------------------------------------

    [Fact]
    public void There_are_eight_categories_with_in_app_on_everywhere_and_email_on_only_for_tasks_overdue_mentions_and_licences()
    {
        Assert.Equal(["workflow_task", "workflow_outcome", "sla", "asset_lifecycle", "mention", "comments", "rights", "translation"], NotificationCategories.All.Select(c => c.Key));
        Assert.All(NotificationCategories.All, c => Assert.True(c.DefaultInApp, c.Key));
        Assert.Equal([true, false, true, false, true, false, true, false], NotificationCategories.All.Select(c => c.DefaultEmail));
        Assert.True(NotificationCategories.IsKnown("sla"));
        Assert.False(NotificationCategories.IsKnown("marketing"));
    }

    [Fact]
    public void A_stored_choice_overrides_the_default_and_a_missing_one_means_the_default()
    {
        var sla = NotificationCategories.Find("sla")!;
        Assert.Equal((true, true), NotificationPreference.Effective(sla, null));
        var off = NotificationPreference.Create(Tenant, Alice, "sla", inApp: true, email: false);
        Assert.Equal((true, false), NotificationPreference.Effective(sla, off));
        off.Set(false, true);
        Assert.Equal((false, true), NotificationPreference.Effective(sla, off));
    }

    // ---- who gets which channel -------------------------------------------------------------------------------

    private static NotificationPreference Pref(Guid user, string category, bool inApp, bool email) => NotificationPreference.Create(Tenant, user, category, inApp, email);

    [Fact]
    public void Planning_uses_defaults_for_people_without_a_choice_and_their_choice_otherwise()
    {
        var task = NotificationCategories.Find("workflow_task")!;
        var stored = new Dictionary<Guid, NotificationPreference> { [Bob] = Pref(Bob, "workflow_task", inApp: true, email: false), [Cara] = Pref(Cara, "workflow_task", inApp: false, email: true) };
        var plan = RecipientPlanner.Plan(task, [Alice, Bob, Cara], stored, emailAvailable: true);
        Assert.Equal([new RecipientPlanner.Delivery(Alice, true, true), new RecipientPlanner.Delivery(Bob, true, false), new RecipientPlanner.Delivery(Cara, false, true)], plan);
    }

    [Fact]
    public void Someone_who_switched_everything_off_is_not_notified_at_all()
    {
        var task = NotificationCategories.Find("workflow_task")!;
        var stored = new Dictionary<Guid, NotificationPreference> { [Alice] = Pref(Alice, "workflow_task", false, false) };
        Assert.Equal([Bob], RecipientPlanner.Plan(task, [Alice, Bob], stored, true).Select(d => d.UserId));
    }

    [Fact]
    public void Email_is_not_planned_when_the_server_cannot_send_it_and_a_person_wanting_only_email_then_gets_nothing()
    {
        var task = NotificationCategories.Find("workflow_task")!;
        var stored = new Dictionary<Guid, NotificationPreference> { [Cara] = Pref(Cara, "workflow_task", false, true) };
        var plan = RecipientPlanner.Plan(task, [Alice, Cara], stored, emailAvailable: false);
        Assert.Equal([new RecipientPlanner.Delivery(Alice, true, false)], plan);
    }

    [Fact]
    public void A_person_listed_twice_is_planned_once()
    {
        var plan = RecipientPlanner.Plan(NotificationCategories.Find("sla")!, [Alice, Alice, Bob], new Dictionary<Guid, NotificationPreference>(), true);
        Assert.Equal([Alice, Bob], plan.Select(d => d.UserId));
    }

    // ---- the rules catalogue ----------------------------------------------------------------------------------

    [Fact]
    public void A_new_task_asks_the_assignee_to_decide_and_links_to_the_inbox()
    {
        var c = NotificationRules.TaskCreated(Asset, "Spring hero", "Review", "Two-level approval", Now.AddHours(48));
        Assert.Equal(("workflow.task.created", "workflow_task", "/tasks", Asset), (c.Type, c.Category, c.LinkPath, c.AssetId));
        Assert.Contains("Spring hero", c.Title);
        Assert.Contains("'Review' step of 'Two-level approval'", c.Body);
        Assert.Contains("21 Oct 2026 12:00 UTC", c.Body);
        Assert.DoesNotContain("due", NotificationRules.TaskCreated(Asset, "x", "Review", "Flow", null).Body);
    }

    [Fact]
    public void An_approval_that_ends_the_workflow_says_approved_at_the_step()
    {
        var c = NotificationRules.TaskCompleted(Asset, "Spring hero", "Final approval", "approve", "approved", null, "Dana");
        Assert.Equal(("workflow_outcome", $"/library/assets/{Asset}"), (c.Category, c.LinkPath));
        Assert.StartsWith("Approved:", c.Title);
        Assert.Contains("approved by Dana at the 'Final approval' step", c.Body);
    }

    [Fact]
    public void An_approval_that_leaves_the_workflow_running_says_the_step_was_approved()
    {
        var c = NotificationRules.TaskCompleted(Asset, "Spring hero", "Review", "approve", "running", "Looks good", null);
        Assert.StartsWith("Step approved:", c.Title);
        Assert.Contains("Comment: Looks good", c.Body);
    }

    [Fact]
    public void A_rejection_carries_the_reviewers_comment()
    {
        var c = NotificationRules.TaskCompleted(Asset, "Spring hero", "Review", "reject", "rejected", "Wrong logo", "Dana");
        Assert.StartsWith("Rejected:", c.Title);
        Assert.Contains("rejected by Dana at the 'Review' step: Wrong logo", c.Body);
    }

    [Fact]
    public void An_overdue_task_is_reported_with_its_deadline()
    {
        var c = NotificationRules.SlaBreached(Asset, "Spring hero", "Review", Now);
        Assert.Equal(("workflow.sla.breached", "sla", "/tasks"), (c.Type, c.Category, c.LinkPath));
        Assert.Contains("19 Oct 2026 12:00 UTC", c.Body);
        Assert.Contains("escalated", c.Body);
    }

    [Fact]
    public void Only_expiring_or_archiving_is_a_lifecycle_notification()
    {
        Assert.StartsWith("Expired:", NotificationRules.AssetLifecycle(Asset, "Spring hero", "expired", "Licence ended")!.Title);
        Assert.Contains("Reason: Licence ended", NotificationRules.AssetLifecycle(Asset, "Spring hero", "expired", "Licence ended")!.Body);
        Assert.StartsWith("Archived:", NotificationRules.AssetLifecycle(Asset, "Spring hero", "archived", null)!.Title);
        foreach (var to in new[] { "draft", "in_review", "approved", "published", "deleted" })
            Assert.Null(NotificationRules.AssetLifecycle(Asset, "x", to, null));
        Assert.Equal("asset_lifecycle", NotificationRules.AssetLifecycle(Asset, "x", "expired", null)!.Category);
    }

    [Fact]
    public void Long_titles_are_shortened_in_the_headline_and_blank_ones_have_a_stand_in()
    {
        Assert.True(NotificationRules.TaskCreated(Asset, new string('a', 300), "S", "W", null).Title.Length < 120);
        Assert.Contains("an asset", NotificationRules.TaskCreated(Asset, "  ", "S", "W", null).Title);
    }

    [Fact]
    public void Chat_descriptions_exist_for_every_catalogue_event()
    {
        foreach (var e in Dam.Domain.Webhooks.EventCatalogue.All.Where(e => e.Name != "webhook.ping"))
        {
            var (title, _) = NotificationRules.Describe(e.Name, "Spring hero", null);
            Assert.DoesNotContain(e.Name, title);   // a readable sentence, not the raw event name
            Assert.Contains("Spring hero", title);
        }
        Assert.Equal("Asset published", NotificationRules.Describe("asset.published", null, null).Title);
    }

    // ---- email templates --------------------------------------------------------------------------------------

    [Fact]
    public void An_email_has_a_subject_with_the_tenant_a_text_part_and_an_html_part_with_the_link()
    {
        var mail = EmailTemplates.Render(new EmailModel("Acme Media", "Approval needed: Spring hero", "It is waiting for you.\n\nPlease decide soon.", "https://orbit.example.com/tasks"));
        Assert.Equal("[Acme Media] Approval needed: Spring hero", mail.Subject);
        Assert.Contains("Acme Media", mail.Text);
        Assert.Contains("Approval needed: Spring hero", mail.Text);
        Assert.Contains("Open in Orbit: https://orbit.example.com/tasks", mail.Text);
        Assert.Contains("Please decide soon.", mail.Text);
        Assert.StartsWith("<!DOCTYPE html>", mail.Html);
        Assert.Contains("href=\"https://orbit.example.com/tasks\"", mail.Html);
        Assert.Contains("<h1", mail.Html);
        Assert.Equal(2, mail.Html.Split("<p style=\"margin:0 0 12px 0;").Length - 1);   // two paragraphs
        Assert.Contains("Acme Media", mail.Html);
    }

    [Fact]
    public void Everything_that_came_from_data_is_escaped_in_the_html()
    {
        var mail = EmailTemplates.Render(new EmailModel("<b>Acme</b>", "<script>alert(1)</script>", "x & y <img src=x onerror=1>", "https://example.com/?a=1&b=\"2\""));
        Assert.DoesNotContain("<script>", mail.Html);
        Assert.DoesNotContain("<img", mail.Html);
        Assert.DoesNotContain("<b>Acme</b>", mail.Html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", mail.Html);
        Assert.Contains("x &amp; y &lt;img", mail.Html);
        Assert.Contains("a=1&amp;b=&quot;2&quot;", mail.Html);
        Assert.Contains("<script>alert(1)</script>", mail.Text);   // plain text is not HTML: nothing to escape there
    }

    [Fact]
    public void A_link_that_is_not_http_is_dropped_and_a_missing_one_leaves_no_button()
    {
        foreach (var bad in new[] { "javascript:alert(1)", "/relative/path", "data:text/html,x", null })
        {
            var mail = EmailTemplates.Render(new EmailModel("Acme", "T", "B", bad));
            Assert.DoesNotContain("<a href", mail.Html);
            Assert.DoesNotContain("Open in Orbit", mail.Text);
        }
    }

    [Fact]
    public void Line_breaks_cannot_be_smuggled_into_the_subject()
    {
        var mail = EmailTemplates.Render(new EmailModel("Acme\r\nBcc: evil@example.com", "Title\nwith break", "B", null));
        Assert.DoesNotContain('\n', mail.Subject);
        Assert.DoesNotContain('\r', mail.Subject);
        Assert.Equal("[Acme Bcc: evil@example.com] Title with break", mail.Subject);
    }

    [Fact]
    public void Absolute_links_join_the_public_address_and_a_console_path()
    {
        Assert.Equal("https://orbit.example.com/tasks", EmailTemplates.Absolute("https://orbit.example.com/", "/tasks"));
        Assert.Equal("https://orbit.example.com/library/assets/1", EmailTemplates.Absolute("https://orbit.example.com", "library/assets/1"));
        Assert.Null(EmailTemplates.Absolute(null, "/tasks"));
        Assert.Null(EmailTemplates.Absolute("not a url", "/tasks"));
    }

    // ---- chat payloads and channel details --------------------------------------------------------------------

    [Fact]
    public void A_slack_message_is_bold_title_body_and_a_link_with_special_characters_escaped()
    {
        var json = JsonSerializer.SerializeToElement(ChatSender.Payload("slack", new Dam.Application.Abstractions.ChatMessage("A & B <tag>", "Body > text", "https://orbit.example.com/x")));
        var text = json.GetProperty("text").GetString()!;
        Assert.Equal("*A &amp; B &lt;tag&gt;*\nBody &gt; text\n<https://orbit.example.com/x|Open in Orbit>", text);
    }

    [Fact]
    public void A_teams_message_carries_one_adaptive_card_with_an_open_url_action()
    {
        var json = JsonSerializer.SerializeToElement(ChatSender.Payload("teams", new Dam.Application.Abstractions.ChatMessage("Title", "Body", "https://orbit.example.com/x")));
        Assert.Equal("message", json.GetProperty("type").GetString());
        var attachment = Assert.Single(json.GetProperty("attachments").EnumerateArray());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal(["Title", "Body"], card.GetProperty("body").EnumerateArray().Select(b => b.GetProperty("text").GetString()));
        Assert.Equal("https://orbit.example.com/x", card.GetProperty("actions")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void A_channel_wants_matching_events_while_active_and_never_shows_its_url()
    {
        var url = "https://hooks.slack.example/services/T000/B000/XXXXSECRETa1b2";
        var channel = NotificationChannel.Create(Tenant, "slack", "Ops", "cipher", NotificationChannel.Hint(url), ["workflow.*"], true, null, Now);
        Assert.True(channel.Wants("workflow.sla.breached"));
        Assert.False(channel.Wants("asset.published"));
        Assert.DoesNotContain("SECRET", channel.UrlHint);
        Assert.EndsWith("a1b2", channel.UrlHint);
        channel.Update("slack", "Ops", ["workflow.*"], false, Now);
        Assert.False(channel.Wants("workflow.sla.breached"));
    }
}

/// <summary>CEF, syslog and CSV formatting of audit rows, which an external SIEM or a spreadsheet will parse (ADR-0031).</summary>
public sealed class AuditFormatTests
{
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset At = new(2026, 10, 19, 12, 30, 45, 123, TimeSpan.Zero);

    private static AuditEntryDto Entry(string action = "asset.status.changed", string actor = "subject-1", string name = "Dana Reviewer", string? after = """{"To":"published"}""", string? ip = "10.1.2.3") =>
        new(Guid.Parse("0197a000-0000-7000-8000-0000000000e1"), 42, At, actor, Guid.Parse("0197a000-0000-7000-8000-0000000000d1"), name, action, "asset",
            Guid.Parse("0197a000-0000-7000-8000-0000000000a1"), null, after is null ? null : JsonDocument.Parse(after).RootElement.Clone(), ip, "hashhash", "prevprev");

    [Fact]
    public void A_cef_line_has_the_seven_header_fields_and_a_readable_extension()
    {
        var line = CefFormatter.Format(Entry(), Tenant, "1.2");
        Assert.StartsWith("CEF:0|Orbit|DAM|1.2|asset.status.changed|Asset status changed|3|", line);
        Assert.Equal(8, SplitUnescaped(line, '|').Count);
        Assert.Contains($"rt={At.ToUnixTimeMilliseconds()}", line);
        Assert.Contains("externalId=0197a000-0000-7000-8000-0000000000e1", line);
        Assert.Contains("suser=Dana Reviewer", line);
        Assert.Contains("suid=subject-1", line);
        Assert.Contains("src=10.1.2.3", line);
        Assert.Contains("cs1Label=entityType cs1=asset", line);
        Assert.Contains("cs4Label=hash cs4=hashhash", line);
        Assert.Contains("cn1Label=chainNo cn1=42", line);
        Assert.Contains($"cs3Label=tenantId cs3={Tenant}", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Header_fields_escape_pipes_and_backslashes_and_extension_values_escape_equals_and_backslashes()
    {
        Assert.Equal(@"a\|b\\c", CefFormatter.Header(@"a|b\c"));
        Assert.Equal(@"a=b", @"a=b");
        Assert.Equal(@"k\=v\\w", CefFormatter.Extension(@"k=v\w"));
        Assert.Equal("a|b", CefFormatter.Extension("a|b"));   // a pipe is only special in the header
        Assert.Equal(@"a=b", CefFormatter.Header("a=b"));       // and an equals sign only in the extension
    }

    [Fact]
    public void Line_breaks_in_any_field_become_backslash_n_so_one_event_is_one_line()
    {
        var line = CefFormatter.Format(Entry(action: "a|b\nc", name: "Line1\r\nLine2=x", after: """{"Note":"two\nlines"}"""), Tenant, "1");
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith(@"CEF:0|Orbit|DAM|1|a\|b\nc|", line);
        Assert.Contains(@"suser=Line1\r\nLine2\=x", line);
    }

    [Fact]
    public void Quotes_and_unicode_in_values_pass_through_and_empty_fields_are_left_out()
    {
        var line = CefFormatter.Format(Entry(name: "Zoë \"Z\" Müller", ip: null, after: null), Tenant, "1");
        Assert.Contains("suser=Zoë \"Z\" Müller", line);
        Assert.DoesNotContain("src=", line);
        Assert.DoesNotContain("msg=", line);
    }

    [Theory]
    [InlineData("asset.status.changed", 3)]
    [InlineData("asset.deleted", 5)]
    [InlineData("webhook.created", 6)]
    [InlineData("webhook.secret_rotated", 6)]
    [InlineData("access_rule.created", 6)]
    [InlineData("role.updated", 6)]
    [InlineData("workflow.sla.breached", 6)]
    [InlineData("asset.legal_hold.changed", 6)]
    [InlineData("ingest.blocked", 7)]
    public void Severity_ranks_governance_changes_above_routine_ones(string action, int severity) => Assert.Equal(severity, CefFormatter.Severity(action));

    [Fact]
    public void A_long_payload_is_cut_in_the_message_field()
    {
        var big = JsonSerializer.Serialize(new { Text = new string('x', 5000) });
        var line = CefFormatter.Format(Entry(after: big), Tenant, "1");
        Assert.True(line.Length < 2500);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Syslog_wraps_a_cef_line_in_an_rfc5424_header_with_local0_and_a_mapped_severity()
    {
        var msg = SyslogFormatter.Format("CEF:0|x", 3, At, "orbit host", "orbit-dam", "audit");
        Assert.Equal("<134>1 2026-10-19T12:30:45.123Z orbithost orbit-dam - audit - CEF:0|x", msg);   // 16*8 + 6 (informational)
        Assert.StartsWith("<133>1", SyslogFormatter.Format("c", 6, At, "h", "a", "m"));   // notice
        Assert.StartsWith("<132>1", SyslogFormatter.Format("c", 7, At, "h", "a", "m"));   // warning
        Assert.StartsWith("<130>1", SyslogFormatter.Format("c", 10, At, "h", "a", "m"));  // critical
        Assert.Contains(" - audit - ", SyslogFormatter.Format("c", 3, At, "", "", "audit").Replace("- - ", "- "));   // empty header fields become "-"
    }

    [Fact]
    public void Framing_is_either_octet_counted_or_newline_delimited()
    {
        var octet = System.Text.Encoding.UTF8.GetString(SyslogFormatter.OctetCounted("héllo"));
        Assert.Equal("6 héllo", octet);   // the length counts bytes: é is two
        Assert.Equal("abc\n", System.Text.Encoding.UTF8.GetString(SyslogFormatter.NewlineDelimited("abc")));
    }

    [Fact]
    public void A_csv_row_has_one_cell_per_column_with_quoting_and_formula_protection()
    {
        Assert.Equal(CsvFormatter.Columns.Length, CsvFormatter.HeaderRow().Split(',').Length);
        Assert.Equal("plain", CsvFormatter.Cell("plain"));
        Assert.Equal("\"a,b\"", CsvFormatter.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvFormatter.Cell("say \"hi\""));
        Assert.Equal("\"two\nlines\"", CsvFormatter.Cell("two\nlines"));
        Assert.Equal("", CsvFormatter.Cell(null));
        foreach (var dangerous in new[] { "=1+1", "+1", "-1", "@SUM(A1)" })
            Assert.StartsWith("'", CsvFormatter.Cell(dangerous));
        var row = CsvFormatter.Row(Entry(name: "=HYPERLINK(\"http://evil\")"));
        Assert.Contains("'=HYPERLINK", row);
        Assert.StartsWith("0197a000-0000-7000-8000-0000000000e1,42,2026-10-19T12:30:45.123000Z,", row);
    }

    /// <summary>Splits a CEF line at pipes that are not escaped.</summary>
    private static List<string> SplitUnescaped(string s, char sep)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { current.Append(s[i]).Append(s[i + 1]); i++; }
            else if (s[i] == sep && parts.Count < 7) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(s[i]);
        }
        parts.Add(current.ToString());
        return parts;
    }
}
