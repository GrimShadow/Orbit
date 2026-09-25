namespace Dam.Domain.Authorization;

/// <summary>The attribute dimensions used for attribute-based access control (spec A4.1 AccessRule).</summary>
public static class Dimensions
{
    public const string Region = "region";
    public const string Dealer = "dealer";
    public const string Brand = "brand";
    public const string Channel = "channel";
    /// <summary>A resource tagged with this value in a dimension is visible to everyone in that dimension.</summary>
    public const string Global = "Global";

    public static readonly IReadOnlyList<string> All = [Region, Dealer, Brand, Channel];
}

public static class AssetStatuses
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Published = "published";
    public const string Expired = "expired";
    public const string Archived = "archived";
    public const string Deleted = "deleted";
}

public enum PrincipalType { User, Group, Role }

public enum ScopeType { All, Folder, Collection, AssetType }

/// <summary>Where an access rule applies. Folder values are ltree-style dotted paths and cover the whole subtree.</summary>
public sealed record ScopeSpec(ScopeType Type, string? Value = null)
{
    public static readonly ScopeSpec Everything = new(ScopeType.All);
    public static ScopeSpec Folder(string path) => new(ScopeType.Folder, path);
    public static ScopeSpec Collection(Guid id) => new(ScopeType.Collection, id.ToString());
    public static ScopeSpec OfAssetType(string assetType) => new(ScopeType.AssetType, assetType);
}

/// <summary>What is being accessed. Only the facts the policy needs, so it works for assets, folders, collections…</summary>
public sealed record ResourceContext
{
    public string Type { get; init; } = "asset";
    public Guid? TenantId { get; init; }
    public string? Status { get; init; }
    public string? FolderPath { get; init; }
    public string? AssetType { get; init; }
    public Guid? OwnerId { get; init; }
    public IReadOnlyCollection<Guid> CollectionIds { get; init; } = [];
    public IReadOnlyDictionary<string, string[]> Attributes { get; init; } = new Dictionary<string, string[]>();

    public static ResourceContext Asset(string status, string? folderPath = null, Guid? ownerId = null,
        string[]? region = null, string[]? dealer = null, string[]? brand = null, string[]? channel = null,
        string? assetType = null, Guid? tenantId = null, IReadOnlyCollection<Guid>? collections = null)
    {
        var attrs = new Dictionary<string, string[]>();
        if (region is not null) attrs[Dimensions.Region] = region;
        if (dealer is not null) attrs[Dimensions.Dealer] = dealer;
        if (brand is not null) attrs[Dimensions.Brand] = brand;
        if (channel is not null) attrs[Dimensions.Channel] = channel;
        return new ResourceContext
        {
            Status = status, FolderPath = folderPath, OwnerId = ownerId, AssetType = assetType, TenantId = tenantId,
            CollectionIds = collections ?? [], Attributes = attrs,
        };
    }
}

/// <summary>A role as the policy sees it (already resolved from the database or the token).</summary>
public sealed record RoleGrant(
    string Name, IReadOnlyCollection<string> Permissions, bool AttributeRestricted = false,
    IReadOnlyList<string>? RequiredAttributes = null);

/// <summary>An access rule as the policy sees it. Grants <see cref="Permissions"/> on resources in <see cref="Scope"/> that match <see cref="Attributes"/>.</summary>
public sealed record RuleGrant(
    Guid RuleId, ScopeSpec Scope, IReadOnlyDictionary<string, string[]> Attributes, IReadOnlyCollection<string> Permissions);

/// <summary>Everything the policy needs to know about one user in one tenant.</summary>
public sealed record AccessProfile(
    Guid TenantId, Guid UserId, bool Active,
    IReadOnlyDictionary<string, string[]> Attributes,
    IReadOnlyList<RoleGrant> Roles, IReadOnlyList<RuleGrant> Rules);
