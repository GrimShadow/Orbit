using System.Text.Json;
using Dam.Application.Comments;
using Dam.Application.Notifications;
using Dam.Domain.Comments;
using Dam.Domain.Common;
using Dam.Domain.Notifications;
using Dam.Domain.Webhooks;

namespace Dam.UnitTests;

/// <summary>Comment anchors, mention text, the activity feed's whitelist, comment notification rules and the Comment aggregate (ADR-0032).</summary>
public sealed class CommentTests
{
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid Asset = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Alice = Guid.Parse("0197a000-0000-7000-8000-0000000000aa");
    private static readonly Guid Bob = Guid.Parse("0197a000-0000-7000-8000-0000000000bb");
    private static readonly Guid Cara = Guid.Parse("0197a000-0000-7000-8000-0000000000cc");
    private static readonly Guid Dan = Guid.Parse("0197a000-0000-7000-8000-0000000000dd");
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    // ---- anchors -----------------------------------------------------------------------------------------------

    private static CommentAnchor Region(double x = 0.12, double y = 0.30, double w = 0.25, double h = 0.18, int? page = null) => new("region", x, y, w, h, page);

    private static string[] Fields(CommentAnchor a, string assetType) => a.Validate(assetType).Keys.Order().ToArray();

    [Fact]
    public void A_region_inside_the_image_is_valid_and_so_are_its_edges()
    {
        Assert.Empty(Fields(Region(), "image"));
        Assert.Empty(Fields(Region(0, 0, 1, 1), "image"));                    // the whole image
        Assert.Empty(Fields(Region(0.7, 0.6, 0.3, 0.4), "image"));            // touches the right and bottom edges
        Assert.Empty(Fields(Region(0.9, 0.9, 0.1 + 1e-12, 0.1), "image"));    // floating-point dust at the edge is not a violation
        Assert.Empty(Fields(Region(0.1, 0.1, 0.5, 0.5, page: 3), "document"));
    }

    [Theory]
    [InlineData(-0.01, 0.3, 0.2, 0.2, "anchor.x")]
    [InlineData(1.01, 0.3, 0.2, 0.2, "anchor.x")]
    [InlineData(0.1, -0.5, 0.2, 0.2, "anchor.y")]
    [InlineData(0.1, 1.5, 0.2, 0.2, "anchor.y")]
    [InlineData(0.1, 0.1, 0, 0.2, "anchor.w")]
    [InlineData(0.1, 0.1, -0.2, 0.2, "anchor.w")]
    [InlineData(0.1, 0.1, 0.2, 0, "anchor.h")]
    [InlineData(0.1, 0.1, 1.5, 0.2, "anchor.w")]
    [InlineData(0.9, 0.1, 0.2, 0.2, "anchor.w")]    // x + w > 1: leaves the image on the right
    [InlineData(0.1, 0.9, 0.2, 0.2, "anchor.h")]    // y + h > 1: leaves the image at the bottom
    [InlineData(double.NaN, 0.1, 0.2, 0.2, "anchor.x")]
    [InlineData(0.1, 0.1, double.PositiveInfinity, 0.2, "anchor.w")]
    public void A_region_outside_the_image_names_the_field_at_fault(double x, double y, double w, double h, string field)
    {
        var errors = Region(x, y, w, h).Validate("image");
        Assert.Contains(field, errors.Keys);
        Assert.All(errors.Values, v => Assert.NotEmpty(v));
    }

    [Fact]
    public void A_region_needs_all_four_numbers()
    {
        Assert.Equal(["anchor.h", "anchor.w", "anchor.x", "anchor.y"], Fields(new CommentAnchor("region"), "image"));
        Assert.Equal(["anchor.w"], Fields(new CommentAnchor("region", 0.1, 0.1, null, 0.2), "image"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void A_page_must_be_between_1_and_10000(int page) => Assert.Equal(["anchor.page"], Fields(Region(page: page), "document"));

    [Fact]
    public void A_point_is_two_fractions_and_nothing_else()
    {
        Assert.Empty(Fields(new CommentAnchor("point", 0.5, 0.5), "image"));
        Assert.Empty(Fields(new CommentAnchor("point", 0, 1, Page: 2), "manual"));
        Assert.Equal(["anchor.x"], Fields(new CommentAnchor("point", 1.2, 0.5), "image"));
        Assert.Equal(["anchor.y"], Fields(new CommentAnchor("point", 0.5, null), "image"));
        Assert.Equal(["anchor.h", "anchor.w"], Fields(new CommentAnchor("point", 0.5, 0.5, 0.1, 0.1), "image"));    // a pin has no size
        Assert.Equal(["anchor.endMs", "anchor.startMs"], Fields(new CommentAnchor("point", 0.5, 0.5, StartMs: 1, EndMs: 2), "image"));
    }

    [Fact]
    public void A_timecode_is_a_start_and_an_optional_later_end_within_a_day()
    {
        Assert.Empty(Fields(new CommentAnchor("timecode", StartMs: 83_500), "video"));
        Assert.Empty(Fields(new CommentAnchor("timecode", StartMs: 83_500, EndMs: 90_000), "video"));
        Assert.Empty(Fields(new CommentAnchor("timecode", StartMs: 0), "audio"));
        Assert.Empty(Fields(new CommentAnchor("timecode", StartMs: CommentAnchor.MaxMs - 1, EndMs: CommentAnchor.MaxMs), "video"));
        Assert.Equal(["anchor.startMs"], Fields(new CommentAnchor("timecode"), "video"));
        Assert.Equal(["anchor.startMs"], Fields(new CommentAnchor("timecode", StartMs: -1), "video"));
        Assert.Equal(["anchor.startMs"], Fields(new CommentAnchor("timecode", StartMs: CommentAnchor.MaxMs + 1), "video"));
        Assert.Equal(["anchor.endMs"], Fields(new CommentAnchor("timecode", StartMs: 5_000, EndMs: 5_000), "video"));     // must be after the start
        Assert.Equal(["anchor.endMs"], Fields(new CommentAnchor("timecode", StartMs: 5_000, EndMs: 4_000), "video"));
        Assert.Equal(["anchor.endMs"], Fields(new CommentAnchor("timecode", StartMs: 5_000, EndMs: CommentAnchor.MaxMs + 1), "video"));
    }

    [Fact]
    public void A_timecode_carries_no_picture_coordinates()
    {
        Assert.Equal(["anchor.page", "anchor.w", "anchor.x"], Fields(new CommentAnchor("timecode", X: 0.1, W: 0.2, Page: 1, StartMs: 1), "video"));
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public void Time_based_assets_refuse_regions_and_points(string type)
    {
        Assert.Equal(["anchor.type"], Fields(Region(), type));
        Assert.Equal(["anchor.type"], Fields(new CommentAnchor("point", 0.5, 0.5), type));
        Assert.Contains("timecode", Region().Validate(type)["anchor.type"][0]);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("document")]
    [InlineData("manual")]
    [InlineData("3d")]
    [InlineData("html5")]
    [InlineData("other")]
    public void Assets_that_are_not_time_based_refuse_timecodes(string type)
    {
        Assert.Equal(["anchor.type"], Fields(new CommentAnchor("timecode", StartMs: 1_000), type));
        Assert.Contains("video and audio", new CommentAnchor("timecode", StartMs: 1_000).Validate(type)["anchor.type"][0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("circle")]
    [InlineData("Region")]
    public void An_unknown_type_is_refused_before_anything_else(string type) => Assert.Equal(["anchor.type"], Fields(new CommentAnchor(type, 5, 5, 5, 5), "image"));

    [Fact]
    public void An_anchor_round_trips_through_its_stored_form_exactly()
    {
        var region = Region(0.12, 0.30, 0.25, 0.18, page: 2);
        Assert.Equal("""{"type":"region","x":0.12,"y":0.3,"w":0.25,"h":0.18,"page":2}""", region.ToJson());
        Assert.Equal(region, CommentAnchor.FromJson(region.ToJson()));

        var time = new CommentAnchor("timecode", StartMs: 83_500, EndMs: null);
        Assert.Equal("""{"type":"timecode","startMs":83500}""", time.ToJson());
        Assert.Equal(time, CommentAnchor.FromJson(time.ToJson()));

        var awkward = Region(0.1 + 0.2, 1.0 / 3.0, 0.123456789012345, 0.5);   // doubles that have no short decimal form
        Assert.Equal(awkward, CommentAnchor.FromJson(awkward.ToJson()));
        Assert.Null(CommentAnchor.FromJson(null));
        Assert.Null(CommentAnchor.FromJson("  "));
    }

    // ---- mention text ------------------------------------------------------------------------------------------

    private static string Token(string name, Guid id) => $"@[{name}](user:{id})";

    [Fact]
    public void Mentions_are_found_once_each_in_order_of_appearance()
    {
        var body = $"Hi {Token("Bob", Bob)}, {Token("Alice", Alice)} and again {Token("Bob B.", Bob)}.";
        Assert.Equal([Bob, Alice], CommentText.MentionedIds(body));
        Assert.Empty(CommentText.MentionedIds("No one here, just @Bob and a (user:abc)."));
    }

    [Fact]
    public void A_token_for_a_person_who_can_see_the_asset_is_kept_and_relabelled_with_their_current_name()
    {
        var eligible = new Dictionary<Guid, string> { [Bob] = "Robert Smith" };
        var stored = CommentText.Normalize($"Ping {Token("Bob", Bob)} please", eligible);
        Assert.Equal($"Ping @[Robert Smith](user:{Bob}) please", stored);   // the label cannot claim a different name than the id has
    }

    [Fact]
    public void A_token_for_anyone_else_becomes_plain_text_that_notifies_nobody()
    {
        var eligible = new Dictionary<Guid, string> { [Bob] = "Bob" };
        var stored = CommentText.Normalize($"{Token("Bob", Bob)} {Token("Mallory", Cara)} {Token("Nobody", Guid.NewGuid())}", eligible);
        Assert.Equal($"@[Bob](user:{Bob}) @Mallory @Nobody", stored);
        Assert.Equal([Bob], CommentText.MentionedIds(stored));              // what remains parses to exactly the eligible people
    }

    [Fact]
    public void Duplicate_and_differently_cased_tokens_collapse_to_one_mention()
    {
        var eligible = new Dictionary<Guid, string> { [Bob] = "Bob" };
        var upper = $"@[bob](user:{Bob.ToString().ToUpperInvariant()})";
        Assert.Equal([Bob], CommentText.MentionedIds($"{Token("Bob", Bob)} {upper}"));
        Assert.Equal($"@[Bob](user:{Bob}) @[Bob](user:{Bob})", CommentText.Normalize($"{Token("Bob", Bob)} {upper}", eligible));
    }

    [Fact]
    public void Malformed_tokens_are_left_alone()
    {
        var body = "@[no id](user:) @[bad](user:not-a-guid) @[](user:0197a000-0000-7000-8000-0000000000bb) @[multi\nline](user:0197a000-0000-7000-8000-0000000000bb)";
        Assert.Empty(CommentText.MentionedIds(body));
        Assert.Equal(body, CommentText.Normalize(body, new Dictionary<Guid, string> { [Bob] = "Bob" }));
    }

    [Fact]
    public void A_name_with_brackets_or_line_breaks_cannot_break_the_token_syntax()
    {
        var t = CommentText.TokenFor("Priya [HQ]\nSingh", Alice);
        Assert.Equal($"@[Priya HQ Singh](user:{Alice})", t);
        Assert.Equal([Alice], CommentText.MentionedIds(t));
        Assert.Equal($"@[user](user:{Alice})", CommentText.TokenFor("  ", Alice));
        Assert.Equal(120, CommentText.TokenFor(new string('x', 500), Alice).Length - $"@[](user:{Alice})".Length);
    }

    [Fact]
    public void The_excerpt_shows_names_not_tokens_on_one_line_and_is_cut_with_an_ellipsis()
    {
        Assert.Equal("Thanks @Bob, can you check\nthis?".Replace("\n", " "), CommentText.Excerpt($"Thanks {Token("Bob", Bob)},   can you check\n\n this?"));
        Assert.Equal("short", CommentText.Excerpt("  short  "));
        var long_ = CommentText.Excerpt(new string('a', 500), 50);
        Assert.Equal(50, long_.Length);
        Assert.EndsWith("…", long_);
        Assert.Equal("@Bob mentioned", CommentText.Plain($"{Token("Bob", Bob)} mentioned"));
    }

    // ---- activity feed -----------------------------------------------------------------------------------------

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Only_whitelisted_audit_actions_become_activity()
    {
        Assert.Null(ActivitySummaries.For("rendition.ready", null));
        Assert.Null(ActivitySummaries.For("ingest.scanned", null));
        Assert.Null(ActivitySummaries.For("upload.completed", null));
        Assert.Null(ActivitySummaries.For("comment.added", null));        // comments come from the comments themselves
        Assert.Null(ActivitySummaries.For("access_rule.created", null));
        Assert.Null(ActivitySummaries.For("workflow.task.created", null));
        Assert.All(ActivitySummaries.Actions, a => Assert.NotNull(ActivitySummaries.For(a, null)));
    }

    [Fact]
    public void Lifecycle_events_have_friendly_phrases_and_kinds()
    {
        Assert.Equal(new ActivitySummary("created", "created the asset"), ActivitySummaries.For("asset.created", Json("""{"AssetId":"x"}""")));
        Assert.Equal(new ActivitySummary("version_added", "uploaded version 3", null, 3), ActivitySummaries.For("asset.version.added", Json("""{"VersionNo":3}""")));
        Assert.Equal(new ActivitySummary("status_changed", "changed the status to in review", "from draft"),
            ActivitySummaries.For("asset.status.changed", Json("""{"From":"draft","To":"in_review"}""")));
        Assert.Equal("updated the details", ActivitySummaries.For("asset.metadata.updated", Json("{}"))!.Text);
        Assert.Equal("placed a legal hold", ActivitySummaries.For("asset.legal_hold.changed", Json("""{"Hold":true}"""))!.Text);
        Assert.Equal("released the legal hold", ActivitySummaries.For("asset.legal_hold.changed", Json("""{"Hold":false}"""))!.Text);
        Assert.Equal(("resolved", "resolved a comment thread"), (ActivitySummaries.For("comment.resolved", null)!.Kind, ActivitySummaries.For("comment.resolved", null)!.Text));
        Assert.Equal("reopened", ActivitySummaries.For("comment.reopened", null)!.Kind);
    }

    [Fact]
    public void A_workflow_decision_shows_the_verdict_and_the_reviewers_comment()
    {
        var approve = ActivitySummaries.For("workflow.task.completed", Json("""{"Decision":"approve","Comment":"Looks good"}"""))!;
        Assert.Equal(("workflow_decision", "approved a workflow step", "Looks good"), (approve.Kind, approve.Text, approve.Detail));
        var reject = ActivitySummaries.For("workflow.task.completed", Json("""{"Decision":"reject","Comment":"Wrong logo"}"""))!;
        Assert.Equal(("rejected a workflow step", "Wrong logo"), (reject.Text, reject.Detail));
        Assert.Null(ActivitySummaries.For("workflow.task.completed", Json("""{"Decision":"approve"}"""))!.Detail);
        Assert.Equal(200, ActivitySummaries.For("workflow.task.completed", Json($$"""{"Decision":"reject","Comment":"{{new string('x', 900)}}"}"""))!.Detail!.Length);
    }

    [Fact]
    public void A_summary_never_carries_the_payload_it_was_made_from()
    {
        var payload = Json("""{"From":"draft","To":"in_review","Secret":"before-after-leak","Ip":"10.0.0.7","Before":{"title":"old title"},"After":{"title":"new title"}}""");
        var s = ActivitySummaries.For("asset.status.changed", payload)!;
        Assert.DoesNotContain("leak", $"{s.Kind} {s.Text} {s.Detail}");
        Assert.DoesNotContain("10.0.0.7", $"{s.Kind} {s.Text} {s.Detail}");
        Assert.DoesNotContain("title", $"{s.Kind} {s.Text} {s.Detail}");
        var meta = ActivitySummaries.For("asset.metadata.updated", Json("""{"Before":{"title":"old title"},"After":{"title":"new title"}}"""))!;
        Assert.Null(meta.Detail);
        Assert.DoesNotContain("title", meta.Text);
    }

    [Fact]
    public void A_cursor_round_trips_and_garbage_is_refused()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        Assert.Equal((at, id), ActivitySummaries.DecodeCursor(ActivitySummaries.EncodeCursor(at, id)));
        Assert.Null(ActivitySummaries.DecodeCursor(null));
        Assert.Null(ActivitySummaries.DecodeCursor(""));
        foreach (var bad in new[] { "abc", "1.2", "x.y", "-5." + id.ToString("N"), "99999999999999999999." + id.ToString("N"), "1." + id })
            Assert.Null(ActivitySummaries.DecodeCursor(bad));
    }

    // ---- notification rules ------------------------------------------------------------------------------------

    [Fact]
    public void A_mention_says_who_and_where_with_an_excerpt_and_a_deep_link()
    {
        var comment = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");
        var c = NotificationRules.Mention("comment.added", Asset, "Acme brochure", comment, "Priya", "Can you check the logo?");
        Assert.Equal(("comment.added", "mention", "Priya mentioned you on 'Acme brochure'", "Can you check the logo?"), (c.Type, c.Category, c.Title, c.Body));
        Assert.Equal($"/library/assets/{Asset}?comment={comment}", c.LinkPath);
        Assert.Equal(Asset, c.AssetId);
        Assert.Equal("comment.edited", NotificationRules.Mention("comment.edited", Asset, "x", comment, "Priya", "y").Type);
    }

    [Fact]
    public void Reply_comment_and_resolved_notifications_are_in_the_comments_category()
    {
        var comment = Guid.NewGuid();
        var reply = NotificationRules.Reply(Asset, "Acme brochure", comment, "Dan", "Fixed");
        Assert.Equal(("comments", "Dan replied to your comment on 'Acme brochure'"), (reply.Category, reply.Title));
        var top = NotificationRules.NewComment(Asset, "Acme brochure", comment, "Dan", "Nice", annotation: false);
        Assert.Equal(("comments", "Dan commented on 'Acme brochure'"), (top.Category, top.Title));
        Assert.Equal("Dan added an annotation to 'Acme brochure'", NotificationRules.NewComment(Asset, "Acme brochure", comment, "Dan", "Nice", annotation: true).Title);
        var done = NotificationRules.ThreadResolved(Asset, "Acme brochure", comment, "Dan", "Logo");
        Assert.Equal(("comment.resolved", "comments", "Dan resolved your comment on 'Acme brochure'"), (done.Type, done.Category, done.Title));
        Assert.All(new[] { reply, top, done }, n => Assert.Equal($"/library/assets/{Asset}?comment={comment}", n.LinkPath));
    }

    [Fact]
    public void Missing_names_and_titles_have_stand_ins_and_long_titles_are_shortened()
    {
        var c = NotificationRules.Mention("comment.added", Asset, "  ", Guid.NewGuid(), null, "x");
        Assert.Equal("Someone mentioned you on an asset", c.Title);
        Assert.True(NotificationRules.Mention("comment.added", Asset, new string('t', 300), Guid.NewGuid(), "Priya", "x").Title.Length < 140);
    }

    [Fact]
    public void Comment_categories_default_to_in_app_always_and_email_for_mentions_only()
    {
        var mention = NotificationCategories.Find("mention")!;
        var comments = NotificationCategories.Find("comments")!;
        Assert.Equal(("Mentions", true, true), (mention.Label, mention.DefaultInApp, mention.DefaultEmail));
        Assert.Equal(("Comments and replies", true, false), (comments.Label, comments.DefaultInApp, comments.DefaultEmail));
        Assert.Equal([new RecipientPlanner.Delivery(Alice, true, true)], RecipientPlanner.Plan(mention, [Alice], new Dictionary<Guid, NotificationPreference>(), true));
        Assert.Equal([new RecipientPlanner.Delivery(Alice, true, false)], RecipientPlanner.Plan(comments, [Alice], new Dictionary<Guid, NotificationPreference>(), true));
        var optedOut = new Dictionary<Guid, NotificationPreference> { [Alice] = NotificationPreference.Create(Tenant, Alice, "mention", inApp: true, email: false) };
        Assert.Equal([new RecipientPlanner.Delivery(Alice, true, false)], RecipientPlanner.Plan(mention, [Alice], optedOut, true));
    }

    // ---- who is told -------------------------------------------------------------------------------------------

    [Fact]
    public void The_author_is_never_told_about_their_own_comment()
    {
        var (mentioned, others) = CommentRecipients.Split(Alice, [Alice, Bob], [Alice, Cara]);
        Assert.Equal([Bob], mentioned);
        Assert.Equal([Cara], others);
    }

    [Fact]
    public void A_person_who_is_mentioned_and_also_concerned_is_told_once_as_mentioned()
    {
        // Bob owns the asset and is also mentioned: one notification, the mention.
        var (mentioned, others) = CommentRecipients.Split(Alice, [Bob], [Bob, Cara]);
        Assert.Equal([Bob], mentioned);
        Assert.Equal([Cara], others);
    }

    [Fact]
    public void Nobody_appears_twice_in_either_group()
    {
        var (mentioned, others) = CommentRecipients.Split(Alice, [Bob, Bob, Cara], [Dan, Dan, Cara]);
        Assert.Equal([Bob, Cara], mentioned);
        Assert.Equal([Dan], others);
        var (none, alsoNone) = CommentRecipients.Split(Alice, [], []);
        Assert.Empty(none);
        Assert.Empty(alsoNone);
    }

    // ---- the aggregate and its events --------------------------------------------------------------------------

    private static Comment NewComment(Guid? parent = null, CommentAnchor? anchor = null, params Guid[] mentions) =>
        Comment.Create(Tenant, Asset, Guid.NewGuid(), parent, Alice, "Hello", anchor, mentions, Now);

    [Fact]
    public void Adding_a_comment_raises_comment_added_with_the_facts_but_never_the_text()
    {
        var anchor = Region();
        var c = NewComment(null, anchor, Bob);
        var e = Assert.IsType<CommentAdded>(Assert.Single(c.DomainEvents));
        Assert.Equal(("comment.added", "comment", c.Id), (e.Name, e.EntityType, e.EntityId));
        Assert.Equal((Asset, Alice, "region", false, c.VersionId, (Guid?)null), (e.AssetId, e.AuthorId, e.AnchorType, e.Resolved, e.VersionId, e.ParentId));
        Assert.Equal([Bob], e.Mentions);
        Assert.DoesNotContain("Hello", JsonSerializer.Serialize(e, e.GetType()));
        Assert.Equal(anchor.ToJson(), c.AnchorJson);
        Assert.True(c.IsRoot);
        Assert.False(NewComment(Guid.NewGuid()).IsRoot);
    }

    [Fact]
    public void Resolving_and_reopening_change_state_once_and_say_who()
    {
        var c = NewComment();
        c.PullEvents();
        Assert.True(c.Resolve(Bob, Now));
        Assert.Equal((true, Bob, Now), (c.Resolved, c.ResolvedBy, c.ResolvedAt));
        var resolved = Assert.IsType<CommentResolved>(Assert.Single(c.PullEvents()));
        Assert.Equal(("comment.resolved", Alice, Bob), (resolved.Name, resolved.AuthorId, resolved.ResolvedBy));

        Assert.False(c.Resolve(Cara, Now));   // already resolved: nothing changes, nothing is raised
        Assert.Empty(c.PullEvents());
        Assert.Equal(Bob, c.ResolvedBy);

        Assert.True(c.Reopen(Cara));
        Assert.Equal((false, (Guid?)null, (DateTimeOffset?)null), (c.Resolved, c.ResolvedBy, c.ResolvedAt));
        Assert.Equal("comment.reopened", Assert.Single(c.PullEvents()).Name);
        Assert.False(c.Reopen(Cara));
        Assert.Empty(c.PullEvents());
    }

    [Fact]
    public void Editing_records_the_time_and_who_was_newly_mentioned_and_ignores_an_unchanged_text()
    {
        var c = NewComment();
        c.PullEvents();
        Assert.False(c.Edit("Hello", [], Now));
        Assert.Null(c.EditedAt);
        Assert.Empty(c.PullEvents());

        Assert.True(c.Edit("Hello again", [Bob], Now.AddMinutes(1)));
        Assert.Equal((Now.AddMinutes(1), "Hello again"), (c.EditedAt, c.Body));
        var e = Assert.IsType<CommentEdited>(Assert.Single(c.PullEvents()));
        Assert.Equal("comment.edited", e.Name);
        Assert.Equal([Bob], e.AddedMentions);
        Assert.False(EventCatalogue.IsKnown(e.Name));   // internal: audit and notifications only
    }

    [Fact]
    public void Deleting_erases_the_text_once_and_is_internal()
    {
        var c = NewComment();
        c.PullEvents();
        c.Delete(Bob, Now);
        Assert.Equal((true, "", Now), (c.IsDeleted, c.Body, c.DeletedAt));
        var e = Assert.IsType<CommentDeleted>(Assert.Single(c.PullEvents()));
        Assert.Equal(("comment.deleted", Bob), (e.Name, e.DeletedBy));
        Assert.False(EventCatalogue.IsKnown(e.Name));
        c.Delete(Cara, Now.AddHours(1));                  // a second delete changes nothing
        Assert.Equal(Now, c.DeletedAt);
        Assert.Empty(c.PullEvents());
    }

    [Fact]
    public void The_public_comment_events_are_in_the_webhook_catalogue_with_descriptions()
    {
        foreach (var name in new[] { "comment.added", "comment.resolved", "comment.reopened" })
        {
            var info = EventCatalogue.All.Single(e => e.Name == name);
            Assert.Equal("comment", info.Category);
            Assert.False(info.Derived);
            Assert.True(info.Description.Length > 20, name);
        }
        var added = EventCatalogue.Expand("comment.added", JsonSerializer.Serialize(NewComment(null, Region(), Bob).DomainEvents[0], typeof(CommentAdded)), Guid.NewGuid(), Now);
        var data = Assert.Single(added).Data;
        Assert.Equal(["anchorType", "assetId", "authorId", "commentId", "entityId", "entityType", "mentions", "name", "parentId", "resolved", "versionId"],
            data.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("body", data.EnumerateObject().Select(p => p.Name));
        Assert.Equal("region", data.GetProperty("anchorType").GetString());
    }
}
