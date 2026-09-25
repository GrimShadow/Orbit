using System.Text.Json;

namespace Dam.Shared.Identity;

/// <summary>Cursor page. Pass <see cref="NextCursor"/> back as ?cursor= for the next page; null means the end.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record TenantDto(Guid Id, string Name, string Slug, string Status, JsonElement Settings, DateTimeOffset CreatedAt);

public sealed record UserDto(
    Guid Id, string ExternalSubject, string Email, string DisplayName, string Status,
    IReadOnlyDictionary<string, string[]> Attributes, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

public sealed record GroupDto(Guid Id, string Name, string Source, IReadOnlyList<string> Roles, int MemberCount);

public sealed record GroupDetailDto(GroupDto Group, IReadOnlyList<UserRef> Members);

public sealed record UserRef(Guid Id, string Email, string DisplayName);

public sealed record RoleDto(
    Guid Id, string Name, string Description, bool IsBuiltIn, IReadOnlyList<string> Permissions,
    bool AttributeRestricted, IReadOnlyList<string> RequiredAttributes);

public sealed record AccessRuleDto(
    Guid Id, string PrincipalType, Guid PrincipalId, string ScopeType, string? ScopeValue,
    IReadOnlyDictionary<string, string[]> Attributes, IReadOnlyList<string> Permissions, DateTimeOffset CreatedAt);

/// <summary>The caller as DAM sees them: provisioned user, effective roles and what they may do.</summary>
public sealed record MeDto(
    Guid? UserId, string? Email, string? DisplayName, Guid TenantId, string? Status,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Groups, IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, string[]> Attributes, int AccessRuleCount,
    /// <summary>Roles held but switched off (e.g. Dealer without a region): they grant nothing until the attribute is set.</summary>
    IReadOnlyList<string> InactiveRoles);
