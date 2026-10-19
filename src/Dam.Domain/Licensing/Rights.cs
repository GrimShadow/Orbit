using System.Text.RegularExpressions;
using Dam.Domain.Authorization;
using Dam.Domain.Common;

namespace Dam.Domain.Licensing;

public static class LicenceTypes
{
    public const string Owned = "owned";
    public const string Licensed = "licensed";
    public const string RoyaltyFree = "royalty_free";
    public const string RightsManaged = "rights_managed";
    public const string Editorial = "editorial";
    public const string CreativeCommons = "creative_commons";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Owned, Licensed, RoyaltyFree, RightsManaged, Editorial, CreativeCommons, Other];

    public static bool IsKnown(string? type) => type is not null && All.Contains(type);
}

/// <summary>Where a licence stands today (ADR-0033).</summary>
public static class RightsStatuses
{
    public const string Valid = "valid";
    /// <summary>Valid, but <c>valid_to</c> is within <see cref="RightsRules.ExpiringWindowDays"/> days.</summary>
    public const string Expiring = "expiring";
    public const string Expired = "expired";
    public const string NotYetValid = "not_yet_valid";

    public static readonly IReadOnlyList<string> All = [Valid, Expiring, Expired, NotYetValid];
}

/// <param name="DaysRemaining">Whole days from today to <c>valid_to</c>: 0 on the last valid day, negative once expired (-1 the day after). Null without an end date.</param>
public readonly record struct RightsValidity(string Status, int? DaysRemaining);

/// <summary>Pure rules for licences: validity, alert thresholds, and the shape of channel and region values.</summary>
public static partial class RightsRules
{
    /// <summary>A licence ending within this many days (inclusive) is "expiring".</summary>
    public const int ExpiringWindowDays = 30;

    /// <summary>Days before <c>valid_to</c> at which the owner is told, largest first. 0 stands for "has expired" in the alert ledger.</summary>
    public static readonly IReadOnlyList<int> AlertThresholds = [30, 7, 1];
    public const int ExpiredThreshold = 0;

    public const int MaxEntries = 50;
    public const int MaxValueLength = 50;

    /// <summary>Today's date in UTC: licence dates are calendar dates, and the end date is inclusive of that whole UTC day.</summary>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    public static RightsValidity Evaluate(DateOnly? validFrom, DateOnly? validTo, DateOnly today)
    {
        int? days = validTo is { } to ? to.DayNumber - today.DayNumber : null;
        if (validFrom is { } from && today < from) return new RightsValidity(RightsStatuses.NotYetValid, days);
        if (days is < 0) return new RightsValidity(RightsStatuses.Expired, days);
        if (days is <= ExpiringWindowDays) return new RightsValidity(RightsStatuses.Expiring, days);
        return new RightsValidity(RightsStatuses.Valid, days);
    }

    /// <summary>
    /// The alert due for a licence with <paramref name="daysRemaining"/> days left: the tightest threshold (30, 7 or 1) that has been reached,
    /// or null when none has (more than 30 days left) or the licence already ended. A licence assigned with 3 days left is due the 7-day
    /// alert, once; the ledger keeps it from repeating.
    /// </summary>
    public static int? AlertThreshold(int daysRemaining)
    {
        if (daysRemaining < 0 || daysRemaining > AlertThresholds[0]) return null;
        var due = AlertThresholds[0];
        foreach (var t in AlertThresholds) if (daysRemaining <= t) due = t;
        return due;
    }

    public static string DaysPhrase(int days) => days switch { <= 0 => "today", 1 => "in 1 day", _ => $"in {days} days" };

    [GeneratedRegex("^[a-z0-9_-]{1,50}$")]
    private static partial Regex Slug();

    public static bool IsSlug(string value) => Slug().IsMatch(value);

    /// <summary>Trims, drops blanks, removes duplicates ignoring case and sorts, keeping the first spelling seen.</summary>
    public static List<string> NormalizeValues(IEnumerable<string>? values) =>
        (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).DistinctBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool SameValues(IEnumerable<string> a, IEnumerable<string> b) =>
        NormalizeValues(a).SequenceEqual(NormalizeValues(b), StringComparer.OrdinalIgnoreCase);

    /// <summary>Field-level problems with a rights definition (keys are the request's camelCase property names). Channel and region values are
    /// checked for shape only here; whether they exist in the tenant's vocabularies is decided by the caller, who can see them.</summary>
    public static Dictionary<string, string[]> Validate(
        string? name, string? licenceType, string? licensor, IReadOnlyList<string>? channels, IReadOnlyList<string>? regions,
        DateOnly? validFrom, DateOnly? validTo, string? notes)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["A name is required."];
        else if (name.Trim().Length > 200) errors["name"] = ["At most 200 characters."];
        if (!LicenceTypes.IsKnown(licenceType)) errors["licenceType"] = [$"Must be one of: {string.Join(", ", LicenceTypes.All)}."];
        if (licensor is { Length: > 200 }) errors["licensor"] = ["At most 200 characters."];
        if (notes is { Length: > 4000 }) errors["notes"] = ["At most 4000 characters."];
        if (validFrom is { } from && validTo is { } to && to < from) errors["validTo"] = ["The end date cannot be before the start date."];
        foreach (var (field, values) in new[] { ("channels", channels), ("regions", regions) })
        {
            var list = NormalizeValues(values);
            if (list.Count > MaxEntries) errors[field] = [$"At most {MaxEntries} values."];
            else if (list.Any(v => v.Length > MaxValueLength)) errors[field] = [$"Each value is at most {MaxValueLength} characters."];
            else if (list.Any(v => string.Equals(v, Dimensions.Global, StringComparison.OrdinalIgnoreCase)))
                errors[field] = ["Leave the list empty to allow everything; 'global' is not a value."];
        }
        return errors;
    }
}

/// <summary>A licence or usage right (spec A4.1 "Rights"). Assets reference it through <c>Asset.RightsId</c>; it restricts the channels and regions
/// they may reach, and when they may be used at all.</summary>
public sealed class Rights : AggregateRoot
{
    private Rights() { }

    public string Name { get; private set; } = "";
    public string LicenceType { get; private set; } = LicenceTypes.Owned;
    public string Licensor { get; private set; } = "";
    /// <summary>Channel codes the licence covers; empty means every channel.</summary>
    public List<string> Channels { get; private set; } = [];
    /// <summary>Region codes the licence covers; empty means worldwide.</summary>
    public List<string> Regions { get; private set; } = [];
    public DateOnly? ValidFrom { get; private set; }
    /// <summary>The last day of use, inclusive: the licence is valid through the end of this UTC day.</summary>
    public DateOnly? ValidTo { get; private set; }
    /// <summary>Whatever the asset's own status, deliveries carry the watermark and the original and video are withheld.</summary>
    public bool WatermarkRequired { get; private set; }
    public string Notes { get; private set; } = "";
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Rights Create(Guid tenantId, string name, string licenceType, string? licensor, IEnumerable<string>? channels, IEnumerable<string>? regions,
        DateOnly? validFrom, DateOnly? validTo, bool watermarkRequired, string? notes, Guid? createdBy, DateTimeOffset now)
    {
        var r = new Rights
        {
            TenantId = tenantId, Name = name.Trim(), LicenceType = licenceType, Licensor = (licensor ?? "").Trim(),
            Channels = RightsRules.NormalizeValues(channels), Regions = RightsRules.NormalizeValues(regions),
            ValidFrom = validFrom, ValidTo = validTo, WatermarkRequired = watermarkRequired, Notes = (notes ?? "").Trim(),
            CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        };
        r.Raise(new RightsCreated(r.Id, r.Name));
        return r;
    }

    /// <summary>Replaces the definition. Returns whether the channels or regions changed, which is when assigned assets must be re-derived.</summary>
    public bool Update(string name, string licenceType, string? licensor, IEnumerable<string>? channels, IEnumerable<string>? regions,
        DateOnly? validFrom, DateOnly? validTo, bool watermarkRequired, string? notes, DateTimeOffset now)
    {
        var newChannels = RightsRules.NormalizeValues(channels);
        var newRegions = RightsRules.NormalizeValues(regions);
        var scopeChanged = !RightsRules.SameValues(Channels, newChannels) || !RightsRules.SameValues(Regions, newRegions);
        var datesChanged = ValidFrom != validFrom || ValidTo != validTo;
        var watermarkChanged = WatermarkRequired != watermarkRequired;
        Name = name.Trim();
        LicenceType = licenceType;
        Licensor = (licensor ?? "").Trim();
        Channels = newChannels;
        Regions = newRegions;
        ValidFrom = validFrom;
        ValidTo = validTo;
        WatermarkRequired = watermarkRequired;
        Notes = (notes ?? "").Trim();
        UpdatedAt = now;
        Raise(new RightsUpdated(Id, Name, scopeChanged, datesChanged, watermarkChanged));
        return scopeChanged;
    }

    public void MarkDeleted() => Raise(new RightsDeleted(Id, Name));

    public RightsValidity Validity(DateOnly today) => RightsRules.Evaluate(ValidFrom, ValidTo, today);
}

/// <summary>A release form or licence file attached to a rights record. The bytes live in object storage under <see cref="StorageKey"/>; they are
/// not virus-scanned (ADR-0033) and are only ever offered as downloads.</summary>
public sealed class RightsDocument : AggregateRoot
{
    private RightsDocument() { }

    public Guid RightsId { get; private set; }
    public string Name { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Bucket { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public Guid? UploadedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static string KeyFor(Guid tenantId, Guid rightsId, Guid documentId) => $"rights/{tenantId:N}/{rightsId:N}/{documentId:N}";

    public static RightsDocument Create(Guid id, Guid tenantId, Guid rightsId, string name, string contentType, long size, string bucket, Guid? uploadedBy, DateTimeOffset now)
    {
        var d = new RightsDocument
        {
            TenantId = tenantId, RightsId = rightsId, Name = name, ContentType = contentType, SizeBytes = size, Bucket = bucket,
            StorageKey = KeyFor(tenantId, rightsId, id), UploadedBy = uploadedBy, CreatedAt = now,
        };
        d.Id = id;
        d.Raise(new RightsDocumentAdded(rightsId, id, name, size));
        return d;
    }

    public void MarkRemoved() => Raise(new RightsDocumentRemoved(RightsId, Id, Name));
}

/// <summary>Allowed release-document types: pdf, png, jpeg, docx and plain text, by declared content type AND file extension, at most 25 MB.</summary>
public static class RightsDocumentPolicy
{
    public const long MaxBytes = 25L * 1024 * 1024;

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ["pdf"],
        ["image/png"] = ["png"],
        ["image/jpeg"] = ["jpg", "jpeg"],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ["docx"],
        ["text/plain"] = ["txt"],
    };

    public static IReadOnlyCollection<string> ContentTypes => Allowed.Keys;

    /// <summary>The problems with a declared document, keyed by request field (<c>fileName</c>, <c>contentType</c>, <c>size</c>); empty when acceptable.</summary>
    public static Dictionary<string, string[]> Validate(string? fileName, string? contentType, long size)
    {
        var errors = new Dictionary<string, string[]>();
        var name = CleanFileName(fileName);
        if (name.Length == 0) errors["fileName"] = ["A file name is required."];
        var type = (contentType ?? "").Split(';')[0].Trim();
        if (!Allowed.TryGetValue(type, out var extensions)) errors["contentType"] = [$"Accepted types: {string.Join(", ", Allowed.Keys)}."];
        else if (name.Length > 0 && !extensions.Contains(Extension(name), StringComparer.OrdinalIgnoreCase))
            errors["fileName"] = [$"A '{type}' file must end in .{string.Join(" or .", extensions)}."];
        if (size <= 0) errors["size"] = ["Size must be greater than zero."];
        else if (size > MaxBytes) errors["size"] = [$"Too large: at most {MaxBytes / (1024 * 1024)} MB."];
        return errors;
    }

    /// <summary>
    /// Whether the first bytes of the stored file agree with the declared type: %PDF, the PNG signature, the JPEG signature, a ZIP container (a docx),
    /// or text with no NUL bytes. A cheap sanity check against a mislabelled upload; it is not a malware scan, and nothing here scans files.
    /// </summary>
    public static bool LooksLike(string contentType, ReadOnlySpan<byte> head) => NormalizeContentType(contentType) switch
    {
        "application/pdf" => head.StartsWith("%PDF-"u8),
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => head.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
        "text/plain" => head.IndexOf((byte)0) < 0,
        _ => false,
    };

    public static string NormalizeContentType(string? contentType) => (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();

    /// <summary>The file name without any directory part or control characters, at most 200 characters.</summary>
    public static string CleanFileName(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim();
        return name.Length <= 200 ? name : name[^200..];
    }

    private static string Extension(string name) => name.LastIndexOf('.') is var i and >= 0 ? name[(i + 1)..] : "";
}

// ---- events. Entity "rights" unless noted. Only the two alert events are public (webhooks, chat); the rest feed the audit trail.

public sealed record RightsCreated(Guid RightsId, string RightsName) : DomainEvent, IEntityEvent
{
    public override string Name => "rights.created";
    public string EntityType => "rights";
    public Guid EntityId => RightsId;
}

public sealed record RightsUpdated(Guid RightsId, string RightsName, bool ScopeChanged, bool DatesChanged, bool WatermarkChanged) : DomainEvent, IEntityEvent
{
    public override string Name => "rights.updated";
    public string EntityType => "rights";
    public Guid EntityId => RightsId;
}

public sealed record RightsDeleted(Guid RightsId, string RightsName) : DomainEvent, IEntityEvent
{
    public override string Name => "rights.deleted";
    public string EntityType => "rights";
    public Guid EntityId => RightsId;
}

public sealed record RightsDocumentAdded(Guid RightsId, Guid DocumentId, string FileName, long SizeBytes) : DomainEvent, IEntityEvent
{
    public override string Name => "rights.document.added";
    public string EntityType => "rights";
    public Guid EntityId => RightsId;
}

public sealed record RightsDocumentRemoved(Guid RightsId, Guid DocumentId, string FileName) : DomainEvent, IEntityEvent
{
    public override string Name => "rights.document.removed";
    public string EntityType => "rights";
    public Guid EntityId => RightsId;
}

/// <summary>An asset's rights assignment, or the restriction it carries, changed; its access attributes were re-derived and it must be re-indexed. Internal.</summary>
public sealed record AssetRightsChanged(Guid AssetId, Guid? RightsId, Guid? PreviousRightsId) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.rights.changed";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>Public. The licence of an asset is about to end (<paramref name="ThresholdDays"/> is the alert reached: 30, 7 or 1).</summary>
public sealed record RightsExpiring(Guid RightsId, Guid AssetId, string RightsName, DateOnly ValidTo, int DaysRemaining, int ThresholdDays) : DomainEvent, IEntityEvent
{
    public const string EventName = "rights.expiring";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>Public. The licence of an asset has ended.</summary>
public sealed record RightsExpired(Guid RightsId, Guid AssetId, string RightsName, DateOnly ValidTo) : DomainEvent, IEntityEvent
{
    public const string EventName = "rights.expired";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>Which alerts have already gone out for an asset's licence, so a job that runs every hour tells the owner once per threshold. Keyed
/// by the licence's end date: extending the licence starts the sequence over.</summary>
public sealed class RightsAlert : TenantEntity
{
    private RightsAlert() { }

    public Guid RightsId { get; private set; }
    public Guid AssetId { get; private set; }
    /// <summary>30, 7 or 1 days before the end, or 0 for "has ended".</summary>
    public int ThresholdDays { get; private set; }
    public DateOnly ValidTo { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
