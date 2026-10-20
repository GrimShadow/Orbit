using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dam.Application.Notifications;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Licensing;
using Dam.Domain.Notifications;
using Dam.Domain.Webhooks;

namespace Dam.UnitTests;

/// <summary>Licence validity, alert thresholds, the effective access attributes a licence gives an asset, the check, definition and document rules, and the signed claims (ADR-0033).</summary>
public sealed class RightsTests
{
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid AssetId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly DateOnly Today = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static Rights Licence(string[]? channels = null, string[]? regions = null, DateOnly? from = null, DateOnly? to = null, bool watermark = false, string name = "Acme stock licence") =>
        Rights.Create(Tenant, name, LicenceTypes.Licensed, "Acme Photos", channels, regions, from, to, watermark, null, null, Now);

    // ---- validity -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-19", "expiring", 0)]         // the end date is inclusive: still usable all through that day, with 0 days left
    [InlineData("2026-10-18", "expired", -1)]         // the day after, it is not
    [InlineData("2026-09-01", "expired", -48)]
    [InlineData("2026-11-18", "expiring", 30)]        // exactly 30 days left: expiring
    [InlineData("2026-11-19", "valid", 31)]           // 31 is not
    [InlineData("2026-10-20", "expiring", 1)]
    [InlineData("2027-10-19", "valid", 365)]
    public void The_end_date_decides_between_valid_expiring_and_expired(string validTo, string status, int days)
    {
        var v = RightsRules.Evaluate(null, DateOnly.Parse(validTo, System.Globalization.CultureInfo.InvariantCulture), Today);
        Assert.Equal((status, days), (v.Status, v.DaysRemaining));
    }

    [Fact]
    public void Open_ends_never_expire_and_have_no_days_remaining()
    {
        Assert.Equal(new RightsValidity(RightsStatuses.Valid, null), RightsRules.Evaluate(null, null, Today));
        Assert.Equal(new RightsValidity(RightsStatuses.Valid, null), RightsRules.Evaluate(new DateOnly(2020, 1, 1), null, Today));
    }

    [Fact]
    public void A_start_date_in_the_future_means_not_yet_valid_and_today_is_the_first_valid_day()
    {
        Assert.Equal(RightsStatuses.NotYetValid, RightsRules.Evaluate(Today.AddDays(1), null, Today).Status);
        Assert.Equal(RightsStatuses.Valid, RightsRules.Evaluate(Today, null, Today).Status);
        var both = RightsRules.Evaluate(Today.AddDays(5), Today.AddDays(40), Today);
        Assert.Equal((RightsStatuses.NotYetValid, 40), (both.Status, both.DaysRemaining));
    }

    [Fact]
    public void Today_is_the_utc_calendar_date_whatever_offset_the_clock_carries()
    {
        Assert.Equal(new DateOnly(2026, 10, 19), RightsRules.Today(new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 20), RightsRules.Today(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 19), RightsRules.Today(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.FromHours(5))));   // 20:00 UTC on the 19th
    }

    // ---- alert thresholds -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(31, null)]
    [InlineData(30, 30)]
    [InlineData(29, 30)]
    [InlineData(8, 30)]
    [InlineData(7, 7)]
    [InlineData(6, 7)]
    [InlineData(2, 7)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-1, null)]
    public void The_tightest_threshold_reached_is_the_one_due(int daysRemaining, int? expected) =>
        Assert.Equal(expected, RightsRules.AlertThreshold(daysRemaining));

    [Fact]
    public void Walking_down_the_days_each_threshold_comes_up_in_turn_and_only_those_three_distinct_values()
    {
        var sequence = Enumerable.Range(0, 40).Reverse().Select(RightsRules.AlertThreshold).Where(t => t is not null).Select(t => t!.Value).ToList();
        Assert.Equal([30, 7, 1], sequence.Distinct());
        Assert.Equal(sequence.OrderByDescending(x => x), sequence);   // never goes back to a looser threshold
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "in 1 day")]
    [InlineData(7, "in 7 days")]
    public void Days_are_phrased_for_a_person(int days, string phrase) => Assert.Equal(phrase, RightsRules.DaysPhrase(days));

    // ---- effective access attributes ------------------------------------------------------------------------------

    public static TheoryData<string[], string[], string[]> Intersections => new()
    {
        { [], [], [] },                                                  // neither side restricts
        { ["Global"], [], ["Global"] },                                  // metadata says everyone, the licence says nothing: unchanged
        { ["web", "signage"], [], ["web", "signage"] },                  // only metadata restricts
        { [], ["web"], ["web"] },                                        // only the licence restricts
        { ["Global"], ["web"], ["web"] },                                // the licence narrows a global asset
        { ["web", "signage"], ["web"], ["web"] },                        // both: the intersection
        { ["web", "signage"], ["signage", "web"], ["signage", "web"] },  // same set
        { ["web"], ["signage"], ["~restricted"] },                       // nothing in common: restricted to nothing
        { ["WEB"], ["web"], ["WEB"] },                                   // case is ignored when comparing
        { ["web", "global"], ["mobile"], ["mobile"] },                   // a Global marker among the metadata values makes that side open
    };

    [Theory]
    [MemberData(nameof(Intersections))]
    public void A_licence_narrows_the_metadata_attribute_to_the_intersection(string[] metadata, string[] licence, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), RightsScope.Effective(metadata, licence).Order(StringComparer.Ordinal));

    [Fact]
    public void Apply_narrows_channel_and_region_and_leaves_every_other_dimension_alone()
    {
        var meta = new Dictionary<string, string[]> { ["channel"] = ["web", "signage"], ["region"] = ["north"], ["brand"] = ["acme"], ["dealer"] = ["d1"] };
        var effective = RightsScope.Apply(meta, ["signage"], ["south"]);
        Assert.Equal(["signage"], effective["channel"]);
        Assert.Equal([RightsScope.RestrictedToNothing], effective["region"]);
        Assert.Equal(["acme"], effective["brand"]);
        Assert.Equal(["d1"], effective["dealer"]);
        Assert.Equal(["web", "signage"], meta["channel"]);   // the input is not changed
    }

    [Fact]
    public void Apply_with_a_licence_that_names_nothing_and_metadata_that_names_nothing_adds_no_keys()
    {
        var effective = RightsScope.Apply(new Dictionary<string, string[]>(), [], []);
        Assert.Empty(effective);
    }

    [Fact]
    public void An_asset_restricted_to_nothing_is_invisible_to_a_restricted_consumer_but_not_to_a_global_role()
    {
        var restricted = new Dictionary<string, string[]> { ["channel"] = [RightsScope.RestrictedToNothing] };
        var signageOnly = new Dictionary<string, string[]> { ["channel"] = ["signage"] };
        Assert.False(PolicyEngine.AttributesAllow(signageOnly, restricted));
        Assert.True(PolicyEngine.AttributesAllow(new Dictionary<string, string[]>(), restricted));    // an unconstrained (global-access) role
        Assert.False(PolicyEngine.AttributesAllow(new Dictionary<string, string[]> { ["channel"] = [] }, restricted));   // even "only Global" does not match
    }

    [Theory]
    [InlineData("web", true)]
    [InlineData("WEB", true)]
    [InlineData("signage", false)]
    public void Allows_checks_one_value_against_the_effective_list(string value, bool expected) => Assert.Equal(expected, RightsScope.Allows(["web"], value));

    [Fact]
    public void Allows_treats_empty_and_global_as_open_and_the_marker_as_closed()
    {
        Assert.True(RightsScope.Allows(null, "web"));
        Assert.True(RightsScope.Allows([], "web"));
        Assert.True(RightsScope.Allows(["Global"], "web"));
        Assert.False(RightsScope.Allows([RightsScope.RestrictedToNothing], "web"));
    }

    [Fact]
    public void The_display_form_hides_the_marker()
    {
        var (web, webNone) = RightsScope.ForDisplay(new Dictionary<string, string[]> { ["channel"] = ["web"] }, "channel");
        Assert.Equal(["web"], web);
        Assert.False(webNone);
        var (none, noneFlag) = RightsScope.ForDisplay(new Dictionary<string, string[]> { ["channel"] = [RightsScope.RestrictedToNothing] }, "channel");
        Assert.Empty(none);
        Assert.True(noneFlag);
        var (open, openFlag) = RightsScope.ForDisplay(new Dictionary<string, string[]>(), "channel");
        Assert.Empty(open);
        Assert.False(openFlag);
    }

    [Fact]
    public void Attribute_sets_compare_by_content_not_by_key_order()
    {
        var a = new Dictionary<string, string[]> { ["channel"] = ["web"], ["region"] = ["north"] };
        var b = new Dictionary<string, string[]> { ["region"] = ["north"], ["channel"] = ["web"] };
        Assert.True(RightsScope.SameAttributes(a, b));
        Assert.False(RightsScope.SameAttributes(a, new Dictionary<string, string[]> { ["channel"] = ["web"] }));
        Assert.False(RightsScope.SameAttributes(a, new Dictionary<string, string[]> { ["channel"] = ["mobile"], ["region"] = ["north"] }));
    }

    // ---- the Asset aggregate ----------------------------------------------------------------------------------------

    private static Asset AssetWith(params (string Dimension, string[] Values)[] attributes)
    {
        var asset = Asset.CreateShell(Tenant, null, "image", "Acme hero", null, Now);
        asset.SetMetadata([], 1, new Asset.Projection([], attributes.ToDictionary(a => a.Dimension, a => a.Values)), Now);
        asset.PullEvents();
        return asset;
    }

    [Fact]
    public void Assigning_rights_narrows_the_assets_attributes_and_asks_for_a_reindex()
    {
        var asset = AssetWith(("channel", ["web", "signage"]), ("region", ["north"]));
        var rights = Licence(["web"]);
        Assert.True(asset.AssignRights(rights.Id, rights.Channels, rights.Regions, Now));

        Assert.Equal(["web"], asset.Attributes["channel"]);
        Assert.Equal(["north"], asset.Attributes["region"]);
        Assert.Equal(["web", "signage"], asset.MetadataAttributes["channel"]);   // the metadata's own answer is kept
        var changed = Assert.IsType<AssetRightsChanged>(Assert.Single(asset.PullEvents()));
        Assert.Equal((asset.Id, rights.Id, (Guid?)null), (changed.AssetId, changed.RightsId, changed.PreviousRightsId));
    }

    [Fact]
    public void Assigning_the_same_rights_again_changes_nothing_and_raises_nothing()
    {
        var asset = AssetWith(("channel", ["web", "signage"]));
        var rights = Licence(["web"]);
        asset.AssignRights(rights.Id, rights.Channels, rights.Regions, Now);
        asset.PullEvents();
        Assert.False(asset.AssignRights(rights.Id, rights.Channels, rights.Regions, Now));
        Assert.Empty(asset.PullEvents());
    }

    [Fact]
    public void A_change_to_the_same_rights_record_s_channels_re_derives_the_asset()
    {
        var asset = AssetWith(("channel", ["web", "signage"]));
        var rights = Licence(["web"]);
        asset.AssignRights(rights.Id, rights.Channels, rights.Regions, Now);
        asset.PullEvents();
        Assert.True(asset.AssignRights(rights.Id, ["signage", "web"], [], Now));
        Assert.Equal(["signage", "web"], asset.Attributes["channel"].Order());
        var changed = Assert.IsType<AssetRightsChanged>(Assert.Single(asset.PullEvents()));
        Assert.Equal(rights.Id, changed.PreviousRightsId);
    }

    [Fact]
    public void Clearing_the_rights_restores_the_metadata_attributes()
    {
        var asset = AssetWith(("channel", ["web", "signage"]));
        var rights = Licence(["web"]);
        asset.AssignRights(rights.Id, rights.Channels, rights.Regions, Now);
        Assert.True(asset.AssignRights(null, ["ignored"], ["ignored"], Now));
        Assert.Equal(["web", "signage"], asset.Attributes["channel"]);
        Assert.Null(asset.RightsId);
        Assert.Empty(asset.RightsChannels);
    }

    [Fact]
    public void Editing_the_metadata_afterwards_still_respects_the_rights()
    {
        var asset = AssetWith(("channel", ["web", "signage"]));
        var rights = Licence(["web"]);
        asset.AssignRights(rights.Id, rights.Channels, rights.Regions, Now);
        asset.SetMetadata([], 1, new Asset.Projection([], new Dictionary<string, string[]> { ["channel"] = ["signage", "mobile"] }), Now);
        Assert.Equal([RightsScope.RestrictedToNothing], asset.Attributes["channel"]);   // nothing in common any more
        asset.SetMetadata([], 1, new Asset.Projection([], new Dictionary<string, string[]> { ["channel"] = ["web", "mobile"] }), Now);
        Assert.Equal(["web"], asset.Attributes["channel"]);
    }

    // ---- checking an asset against a consumer ---------------------------------------------------------------------

    private static RightsCheck Check(Rights? rights, Dictionary<string, string[]> attributes, string? channel = null, string? region = null, DateOnly? today = null) =>
        RightsChecker.Check(rights, attributes, channel, region, today ?? Today);

    [Fact]
    public void An_asset_without_rights_is_allowed_unless_its_attributes_say_otherwise()
    {
        Assert.True(Check(null, new Dictionary<string, string[]>(), "web", "north").Allowed);
        var web = new Dictionary<string, string[]> { ["channel"] = ["web"] };
        Assert.True(Check(null, web, "web").Allowed);
        var blocked = Check(null, web, "signage");
        Assert.False(blocked.Allowed);
        Assert.Equal("This asset is not available for the 'signage' channel.", Assert.Single(blocked.Reasons));
    }

    [Fact]
    public void The_licence_s_channels_and_regions_must_contain_the_value_and_the_reason_names_the_licence()
    {
        var rights = Licence(["web", "print"], ["north"]);
        var attributes = RightsScope.Apply(new Dictionary<string, string[]>(), rights.Channels, rights.Regions);
        Assert.True(Check(rights, attributes, "web", "north").Allowed);
        var wrongChannel = Check(rights, attributes, "signage", "north");
        Assert.False(wrongChannel.ChannelAllowed);
        Assert.True(wrongChannel.RegionAllowed);
        Assert.Equal("The licence 'Acme stock licence' does not cover the 'signage' channel (it covers: print, web).", Assert.Single(wrongChannel.Reasons));
        var both = Check(rights, attributes, "signage", "south");
        Assert.Equal(2, both.Reasons.Count);
    }

    [Fact]
    public void A_licence_that_names_nothing_allows_any_channel_and_region()
    {
        var rights = Licence();
        Assert.True(Check(rights, new Dictionary<string, string[]>(), "anything", "anywhere").Allowed);
    }

    [Fact]
    public void An_asset_restricted_to_nothing_allows_no_channel_but_is_fine_when_no_channel_is_asked_about()
    {
        var attributes = new Dictionary<string, string[]> { ["channel"] = [RightsScope.RestrictedToNothing] };
        Assert.False(Check(null, attributes, "web").Allowed);
        Assert.True(Check(null, attributes).Allowed);
    }

    [Fact]
    public void Expired_and_not_yet_valid_licences_are_reasons_in_themselves()
    {
        var ended = Check(Licence(to: Today.AddDays(-1)), new Dictionary<string, string[]>());
        Assert.True(ended.Expired);
        Assert.True(ended.OutsideValidity);
        Assert.False(ended.Allowed);
        Assert.Equal(RightsStatuses.Expired, ended.Status);
        Assert.Equal("The licence 'Acme stock licence' ended on 2026-10-18.", Assert.Single(ended.Reasons));

        var future = Check(Licence(from: Today.AddDays(3)), new Dictionary<string, string[]>());
        Assert.True(future.NotYetValid);
        Assert.Equal("The licence 'Acme stock licence' starts on 2026-10-22.", Assert.Single(future.Reasons));

        var lastDay = Check(Licence(to: Today), new Dictionary<string, string[]>());
        Assert.True(lastDay.Allowed);   // the end date is inclusive
        Assert.Equal(0, lastDay.DaysRemaining);
    }

    [Fact]
    public void The_check_reports_whether_a_watermark_is_required()
    {
        Assert.True(Check(Licence(watermark: true), new Dictionary<string, string[]>()).WatermarkRequired);
        Assert.False(Check(Licence(), new Dictionary<string, string[]>()).WatermarkRequired);
        Assert.False(RightsCheck.NoRights.WatermarkRequired);
        Assert.True(RightsCheck.NoRights.Allowed);
    }

    // ---- definition rules -----------------------------------------------------------------------------------------

    private static Dictionary<string, string[]> Validate(string? name = "Acme licence", string? type = "licensed", string? licensor = "Acme", string[]? channels = null,
        string[]? regions = null, DateOnly? from = null, DateOnly? to = null, string? notes = null) =>
        RightsRules.Validate(name, type, licensor, channels, regions, from, to, notes);

    [Fact]
    public void A_complete_definition_has_no_problems() => Assert.Empty(Validate(channels: ["web"], regions: ["north"], from: Today, to: Today.AddDays(10)));

    [Fact]
    public void A_single_day_licence_is_fine_but_an_end_before_the_start_is_not()
    {
        Assert.Empty(Validate(from: Today, to: Today));
        Assert.Contains("validTo", Validate(from: Today, to: Today.AddDays(-1)).Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_is_required(string? name) => Assert.Contains("name", Validate(name: name).Keys);

    [Fact]
    public void Lengths_and_the_licence_type_are_checked()
    {
        Assert.Contains("name", Validate(name: new string('x', 201)).Keys);
        Assert.Contains("licensor", Validate(licensor: new string('x', 201)).Keys);
        Assert.Contains("notes", Validate(notes: new string('x', 4001)).Keys);
        Assert.Contains("licenceType", Validate(type: "free").Keys);
        Assert.Contains("licenceType", Validate(type: null).Keys);
        foreach (var type in LicenceTypes.All) Assert.Empty(Validate(type: type));
    }

    [Fact]
    public void Channel_and_region_lists_are_limited_and_may_not_contain_global()
    {
        Assert.Contains("channels", Validate(channels: ["global"]).Keys);
        Assert.Contains("regions", Validate(regions: ["Global"]).Keys);
        Assert.Contains("channels", Validate(channels: Enumerable.Range(0, 51).Select(i => $"c{i}").ToArray()).Keys);
        Assert.Contains("regions", Validate(regions: [new string('x', 51)]).Keys);
        Assert.Empty(Validate(channels: Enumerable.Range(0, 50).Select(i => $"c{i}").ToArray()));
    }

    [Theory]
    [InlineData("web", true)]
    [InlineData("print_2", true)]
    [InlineData("north-east", true)]
    [InlineData("Web", false)]     // slugs are lower case; the caller lower-cases before asking
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Without_a_vocabulary_a_value_is_a_lower_case_slug(string value, bool ok) => Assert.Equal(ok, RightsRules.IsSlug(value));

    [Fact]
    public void Normalising_values_trims_drops_blanks_and_duplicates_and_sorts()
    {
        Assert.Equal(["north", "South"], RightsRules.NormalizeValues([" South", "north", "NORTH", "", "  "]));
        Assert.Empty(RightsRules.NormalizeValues(null));
        Assert.True(RightsRules.SameValues(["a", "B"], ["b", "A"]));
        Assert.False(RightsRules.SameValues(["a"], ["a", "b"]));
    }

    [Fact]
    public void Updating_a_record_reports_whether_its_scope_changed()
    {
        var rights = Licence(["web"], ["north"], to: Today.AddDays(10));
        rights.PullEvents();
        Assert.False(rights.Update("Renamed", LicenceTypes.Licensed, "Acme", ["WEB"], ["north"], null, Today.AddDays(20), true, "n", Now));      // case and dates are not scope
        var first = Assert.IsType<RightsUpdated>(Assert.Single(rights.PullEvents()));
        Assert.Equal((false, true, true), (first.ScopeChanged, first.DatesChanged, first.WatermarkChanged));
        Assert.True(rights.Update("Renamed", LicenceTypes.Licensed, "Acme", ["web", "print"], ["north"], null, Today.AddDays(20), true, "n", Now));
        Assert.True(Assert.IsType<RightsUpdated>(Assert.Single(rights.PullEvents())).ScopeChanged);
    }

    // ---- documents ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("form.pdf", "application/pdf")]
    [InlineData("scan.png", "image/png")]
    [InlineData("scan.jpg", "image/jpeg")]
    [InlineData("scan.JPEG", "image/jpeg")]
    [InlineData("licence.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("notes.txt", "text/plain; charset=utf-8")]
    public void The_five_document_types_are_accepted_when_the_extension_agrees_with_the_declared_type(string name, string type) =>
        Assert.Empty(RightsDocumentPolicy.Validate(name, type, 1000));

    [Theory]
    [InlineData("form.exe", "application/pdf")]            // extension does not match the type
    [InlineData("form.pdf", "image/png")]
    [InlineData("page.html", "text/html")]                 // type not on the list
    [InlineData("macro.docm", "application/vnd.ms-word.document.macroEnabled.12")]
    [InlineData("form.pdf", "")]
    [InlineData("noextension", "application/pdf")]
    public void Anything_else_is_refused(string name, string type) => Assert.NotEmpty(RightsDocumentPolicy.Validate(name, type, 1000));

    [Fact]
    public void Size_must_be_positive_and_at_most_25_mb()
    {
        Assert.Empty(RightsDocumentPolicy.Validate("a.pdf", "application/pdf", RightsDocumentPolicy.MaxBytes));
        Assert.Contains("size", RightsDocumentPolicy.Validate("a.pdf", "application/pdf", RightsDocumentPolicy.MaxBytes + 1).Keys);
        Assert.Contains("size", RightsDocumentPolicy.Validate("a.pdf", "application/pdf", 0).Keys);
        Assert.Contains("fileName", RightsDocumentPolicy.Validate(null, "application/pdf", 10).Keys);
        Assert.Equal(25L * 1024 * 1024, RightsDocumentPolicy.MaxBytes);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\forms\\a.pdf", "a.pdf")]
    [InlineData("a\"b.pdf", "ab.pdf")]
    [InlineData("  spaced.pdf  ", "spaced.pdf")]
    public void File_names_lose_their_directories_quotes_and_padding(string raw, string clean) => Assert.Equal(clean, RightsDocumentPolicy.CleanFileName(raw));

    [Fact]
    public void A_file_whose_first_bytes_disagree_with_its_declared_type_is_caught()
    {
        Assert.True(RightsDocumentPolicy.LooksLike("application/pdf", "%PDF-1.7 ..."u8));
        Assert.False(RightsDocumentPolicy.LooksLike("application/pdf", "<html>"u8));
        Assert.True(RightsDocumentPolicy.LooksLike("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.True(RightsDocumentPolicy.LooksLike("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.False(RightsDocumentPolicy.LooksLike("image/jpeg", new byte[] { 0x89, 0x50 }));
        Assert.True(RightsDocumentPolicy.LooksLike("application/vnd.openxmlformats-officedocument.wordprocessingml.document", new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
        Assert.True(RightsDocumentPolicy.LooksLike("text/plain", "plain words"u8));
        Assert.False(RightsDocumentPolicy.LooksLike("text/plain", new byte[] { 0x41, 0x00, 0x42 }));
        Assert.False(RightsDocumentPolicy.LooksLike("text/html", "<html>"u8));
    }

    [Fact]
    public void A_documents_object_key_is_built_from_ids_only_under_the_rights_prefix()
    {
        var rights = Guid.Parse("0197a000-0000-7000-8000-0000000000b1");
        var doc = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");
        Assert.Equal($"rights/{Tenant:N}/{rights:N}/{doc:N}", RightsDocument.KeyFor(Tenant, rights, doc));
    }

    [Theory]
    [InlineData("release.pdf", "attachment; filename=\"release.pdf\"; filename*=UTF-8''release.pdf")]
    [InlineData("bad\"name;x.pdf", "attachment; filename=\"badnamex.pdf\"; filename*=UTF-8''badnamex.pdf")]
    [InlineData("a/b\\c.pdf", "attachment; filename=\"abc.pdf\"; filename*=UTF-8''abc.pdf")]
    [InlineData("", "attachment; filename=\"download\"; filename*=UTF-8''download")]
    public void A_download_header_is_always_an_attachment_whatever_the_file_is_called(string name, string expected) =>
        Assert.Equal(expected, Dam.Infrastructure.Storage.S3ObjectStore.AttachmentHeader(name));

    [Fact]
    public void A_non_ascii_name_is_offered_in_both_forms_and_a_header_injection_is_impossible()
    {
        var header = Dam.Infrastructure.Storage.S3ObjectStore.AttachmentHeader("Lizenz \u00fcber\r\nSet-Cookie: x=1.pdf");
        Assert.DoesNotContain("\r", header, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", header, StringComparison.Ordinal);
        Assert.StartsWith("attachment; filename=\"Lizenz _berSet-Cookie: x=1.pdf\"", header, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''Lizenz%20%C3%BCberSet-Cookie%3A%20x%3D1.pdf", header, StringComparison.Ordinal);
    }

    // ---- signed claims --------------------------------------------------------------------------------------------

    private const string Key = "unit-test-signing-key-0123456789abcdef";

    [Fact]
    public void Channel_and_region_round_trip_through_a_signed_token()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var claims = new DeliveryClaims(Tenant, AssetId, "r:thumb", Now.AddMinutes(10), null, true, "signage", "north");
        var (back, failure) = signer.Verify(signer.Sign(claims), Now);
        Assert.Equal(TokenFailure.None, failure);
        Assert.Equal(("signage", "north", true), (back!.Channel, back.Region, back.Watermark));
    }

    [Fact]
    public void A_token_without_channel_and_region_is_byte_for_byte_what_it_was_before_they_existed()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var token = signer.Sign(new DeliveryClaims(Tenant, AssetId, "r:thumb", Now.AddMinutes(10), null, false));
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(token.Split('.')[0])));
        Assert.DoesNotContain("\"C\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"R\"", payload, StringComparison.Ordinal);
        var (back, _) = signer.Verify(token, Now);
        Assert.Null(back!.Channel);
        Assert.Null(back.Region);
    }

    [Fact]
    public void A_token_signed_by_the_previous_release_still_verifies()
    {
        // Exactly what the code before Step 2.5 produced: a payload with no C and no R.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { T = Tenant.ToString("N"), A = AssetId.ToString("N"), S = "r:preview", E = Now.AddMinutes(30).ToUnixTimeSeconds(), Ip = (string?)null, W = 1 });
        var body = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = body + "." + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.ASCII.GetBytes(body))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var (claims, failure) = new DeliveryTokenSigner([Key]).Verify(token, Now);
        Assert.Equal(TokenFailure.None, failure);
        Assert.Equal((Tenant, AssetId, "r:preview", true, (string?)null, (string?)null), (claims!.TenantId, claims.AssetId, claims.Scope, claims.Watermark, claims.Channel, claims.Region));
    }

    [Fact]
    public void Changing_the_channel_in_a_signed_token_breaks_the_signature()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var token = signer.Sign(new DeliveryClaims(Tenant, AssetId, "r:thumb", Now.AddMinutes(10), null, false, "web"));
        var parts = token.Split('.');
        var tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[0]))).Replace("\"web\"", "\"app\"", StringComparison.Ordinal))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(TokenFailure.BadSignature, signer.Verify(tampered + "." + parts[1], Now).Failure);
    }

    private static string Pad(string s) => s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    // ---- events, notifications ------------------------------------------------------------------------------------

    [Fact]
    public void The_two_alert_events_are_public_and_the_housekeeping_events_are_not()
    {
        foreach (var name in new[] { RightsExpiring.EventName, RightsExpired.EventName })
        {
            Assert.Contains(name, EventCatalogue.SourceEvents);
            Assert.False(string.IsNullOrWhiteSpace(EventCatalogue.All.Single(e => e.Name == name).Description));
            Assert.Equal("rights", EventCatalogue.All.Single(e => e.Name == name).Category);
        }
        foreach (var name in new[] { "rights.created", "rights.updated", "rights.deleted", "rights.document.added", "rights.document.removed", AssetRightsChanged.EventName })
            Assert.False(EventCatalogue.IsKnown(name), name);
        Assert.True(EventPattern.Matches("rights.*", "rights.expiring"));
    }

    [Fact]
    public void Licence_alerts_are_a_category_that_is_on_in_the_console_and_by_email()
    {
        var category = NotificationCategories.Find(NotificationCategories.Rights)!;
        Assert.Equal(("rights", "Licence alerts", true, true), (category.Key, category.Label, category.DefaultInApp, category.DefaultEmail));
    }

    [Fact]
    public void The_expiring_notification_names_the_asset_the_days_and_links_to_the_asset()
    {
        var n = NotificationRules.LicenceExpiring(AssetId, "Acme hero", "Acme stock licence", new DateOnly(2026, 10, 26), 7);
        Assert.Equal("Licence for 'Acme hero' expires in 7 days", n.Title);
        Assert.Equal((NotificationCategories.Rights, $"/library/assets/{AssetId}", AssetId), (n.Category, n.LinkPath, n.AssetId));
        Assert.Contains("'Acme stock licence'", n.Body, StringComparison.Ordinal);
        Assert.Contains("26 Oct 2026", n.Body, StringComparison.Ordinal);
        Assert.Equal("rights.expiring", n.Type);
        Assert.Equal("Licence for 'Acme hero' expires in 1 day", NotificationRules.LicenceExpiring(AssetId, "Acme hero", "L", new DateOnly(2026, 10, 20), 1).Title);
        Assert.Equal("Licence for 'Acme hero' expires today", NotificationRules.LicenceExpiring(AssetId, "Acme hero", "L", new DateOnly(2026, 10, 19), 0).Title);
    }

    [Fact]
    public void The_expired_notification_says_the_licence_has_ended()
    {
        var n = NotificationRules.LicenceExpired(AssetId, "Acme hero", "Acme stock licence", new DateOnly(2026, 10, 18));
        Assert.Equal("Licence for 'Acme hero' has expired", n.Title);
        Assert.Equal("rights.expired", n.Type);
        Assert.Contains("18 Oct 2026", n.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_alert_events_serialise_with_the_fields_webhook_receivers_are_told_about()
    {
        var json = JsonSerializer.Serialize(new RightsExpiring(Guid.NewGuid(), AssetId, "Acme stock licence", new DateOnly(2026, 10, 26), 7, 7));
        var expanded = EventCatalogue.Expand("rights.expiring", json, Guid.NewGuid(), Now).Single();
        Assert.Equal("rights.expiring", expanded.Type);
        var data = expanded.Data;
        Assert.Equal(("Acme stock licence", "2026-10-26", 7, 7, AssetId), (data.GetProperty("rightsName").GetString(), data.GetProperty("validTo").GetString(), data.GetProperty("daysRemaining").GetInt32(),
            data.GetProperty("thresholdDays").GetInt32(), data.GetProperty("assetId").GetGuid()));
    }
}
