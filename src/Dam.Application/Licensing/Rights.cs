using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Licensing;
using Dam.Shared.Licensing;

namespace Dam.Application.Licensing;

/// <summary>Rights records are managed by whoever holds <c>rights.manage</c> (Admin and Librarian); they are not scoped to folders.</summary>
internal static class RightsAccess
{
    /// <summary>Assets whose access attributes one request re-derives when a record's channels or regions change; the rest are left to the hourly rights job.</summary>
    public const int MaxInlineRederive = 500;

    public static async Task<Rights?> FindAsync(IRepository<Rights> rights, IQueryExecutor q, Guid id, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(rights.Query().Where(r => r.Id == id), ct);

    public static Task<bool> NameTakenAsync(IRepository<Rights> rights, IQueryExecutor q, string name, Guid? except, CancellationToken ct)
    {
        var lower = name.Trim().ToLowerInvariant();
        return q.AnyAsync(rights.Query().Where(r => r.Name.ToLower() == lower && (except == null || r.Id != except)), ct);
    }
}

// ------------------------------------------------------------------------------------------------------- list / get

/// <summary>Every rights record of the tenant with its status today and how many assets and documents it has. <paramref name="Status"/> narrows to
/// valid, expiring, expired or not_yet_valid; <paramref name="Q"/> matches names containing the text.</summary>
public sealed record ListRightsQuery(string? Status, string? Q) : IQuery<IReadOnlyList<RightsDto>>;

public sealed class ListRightsHandler(IRepository<Rights> rights, IQueryExecutor q, IAuthorizationService authz, RightsMapper mapper)
    : IRequestHandler<ListRightsQuery, IReadOnlyList<RightsDto>>
{
    public async Task<Result<IReadOnlyList<RightsDto>>> Handle(ListRightsQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        if (r.Status is not null && !RightsStatuses.All.Contains(r.Status))
            return Error.Validation(ContentMapping.Fail("status", $"Must be one of: {string.Join(", ", RightsStatuses.All)}."));
        var query = rights.Query();
        if (r.Q is { Length: > 0 } text) { var lower = text.Trim().ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().Contains(lower)); }
        var list = await q.ToListAsync(query.OrderBy(x => x.Name), ct);
        var dtos = await mapper.ToDtosAsync(list, null, ct);
        return r.Status is null ? dtos : dtos.Where(d => d.Status == r.Status).ToList();
    }
}

public sealed record GetRightsQuery(Guid Id) : IQuery<RightsDto>;

public sealed class GetRightsHandler(IRepository<Rights> rights, IQueryExecutor q, IAuthorizationService authz, RightsMapper mapper)
    : IRequestHandler<GetRightsQuery, RightsDto>
{
    public async Task<Result<RightsDto>> Handle(GetRightsQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var found = await RightsAccess.FindAsync(rights, q, r.Id, ct);
        return found is null ? Error.NotFound("Rights record not found.") : await mapper.ToDtoAsync(found, 0, ct);
    }
}

// --------------------------------------------------------------------------------------------------------- create

public sealed record CreateRightsCommand(RightsInput Input) : ICommand<RightsDto>;

public sealed class CreateRightsHandler(
    IRepository<Rights> rights, IQueryExecutor q, IAuthorizationService authz, RightsVocabularies vocabularies, RightsMapper mapper,
    ITenantContext tenant, ICurrentUser me, IClock clock) : IRequestHandler<CreateRightsCommand, RightsDto>
{
    public async Task<Result<RightsDto>> Handle(CreateRightsCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var i = c.Input;
        var errors = RightsRules.Validate(i.Name, i.LicenceType, i.Licensor, i.Channels, i.Regions, i.ValidFrom, i.ValidTo, i.Notes);
        var (channels, regions, vocabErrors) = await vocabularies.NormalizeAsync(i.Channels, i.Regions, ct);
        foreach (var (k, v) in vocabErrors) errors.TryAdd(k, v);
        if (!errors.ContainsKey("name") && await RightsAccess.NameTakenAsync(rights, q, i.Name!, null, ct)) errors["name"] = ["A rights record with this name already exists."];
        if (errors.Count > 0) return Error.Validation(errors);

        var created = Rights.Create(tenant.TenantId, i.Name!, i.LicenceType!, i.Licensor, channels, regions, i.ValidFrom, i.ValidTo, i.WatermarkRequired, i.Notes, me.UserId, clock.UtcNow);
        rights.Add(created);
        return await mapper.ToDtoAsync(created, 0, ct);
    }
}

// --------------------------------------------------------------------------------------------------------- update

/// <summary>
/// Replaces a rights record. When its channels or regions change, every assigned asset's access attributes are re-derived in this same transaction
/// (and each is re-indexed through its <c>asset.rights.changed</c> event), for up to <see cref="RightsAccess.MaxInlineRederive"/> assets; any beyond
/// that are reported in <c>pendingAssets</c> and caught up by the hourly rights job.
/// </summary>
public sealed record UpdateRightsCommand(Guid Id, RightsInput Input) : ICommand<RightsDto>;

public sealed class UpdateRightsHandler(
    IRepository<Rights> rights, IRepository<Asset> assets, IQueryExecutor q, IAuthorizationService authz, RightsVocabularies vocabularies, RightsMapper mapper, IClock clock)
    : IRequestHandler<UpdateRightsCommand, RightsDto>
{
    public async Task<Result<RightsDto>> Handle(UpdateRightsCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var record = await RightsAccess.FindAsync(rights, q, c.Id, ct);
        if (record is null) return Error.NotFound("Rights record not found.");

        var i = c.Input;
        var errors = RightsRules.Validate(i.Name, i.LicenceType, i.Licensor, i.Channels, i.Regions, i.ValidFrom, i.ValidTo, i.Notes);
        var (channels, regions, vocabErrors) = await vocabularies.NormalizeAsync(i.Channels, i.Regions, ct);
        foreach (var (k, v) in vocabErrors) errors.TryAdd(k, v);
        if (!errors.ContainsKey("name") && await RightsAccess.NameTakenAsync(rights, q, i.Name!, c.Id, ct)) errors["name"] = ["A rights record with this name already exists."];
        if (errors.Count > 0) return Error.Validation(errors);

        var now = clock.UtcNow;
        var scopeChanged = record.Update(i.Name!, i.LicenceType!, i.Licensor, channels, regions, i.ValidFrom, i.ValidTo, i.WatermarkRequired, i.Notes, now);

        var pending = 0;
        if (scopeChanged)
        {
            var assigned = assets.Query().Where(a => a.RightsId == c.Id && a.Status != AssetStatuses.Deleted);
            var total = await q.CountAsync(assigned, ct);
            foreach (var a in await q.ToListAsync(assigned.OrderBy(a => a.Id).Take(RightsAccess.MaxInlineRederive), ct))
                a.AssignRights(record.Id, record.Channels, record.Regions, now);
            pending = Math.Max(0, total - RightsAccess.MaxInlineRederive);
        }
        return await mapper.ToDtoAsync(record, pending, ct);
    }
}

// --------------------------------------------------------------------------------------------------------- delete

/// <summary>Deletes a rights record and its documents. Refused (409, with the count) while any live asset still uses it.</summary>
public sealed record DeleteRightsCommand(Guid Id) : ICommand<Guid>;

public sealed class DeleteRightsHandler(
    IRepository<Rights> rights, IRepository<Asset> assets, IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz, IObjectStore store, IClock clock)
    : IRequestHandler<DeleteRightsCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteRightsCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var record = await RightsAccess.FindAsync(rights, q, c.Id, ct);
        if (record is null) return Error.NotFound("Rights record not found.");

        var live = await q.CountAsync(assets.Query().Where(a => a.RightsId == c.Id && a.Status != AssetStatuses.Deleted), ct);
        if (live > 0)
            return new Error("conflict", $"This rights record is still assigned to {live} asset(s). Unassign them first.", ErrorType.Conflict,
                Extensions: new Dictionary<string, object?> { ["assetCount"] = live });

        // Deleted assets keep their row (retention decides about the bytes) and with it a reference; let go of it so the record can go.
        foreach (var a in await q.ToListAsync(assets.Query().Where(a => a.RightsId == c.Id), ct)) a.AssignRights(null, [], [], clock.UtcNow);

        var docs = await q.ToListAsync(documents.Query().Where(d => d.RightsId == c.Id), ct);
        foreach (var d in docs) d.MarkRemoved();
        record.MarkDeleted();
        documents.RemoveRange(docs);
        rights.Remove(record);
        foreach (var d in docs)
            try { await store.DeleteAsync(d.Bucket, d.StorageKey, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* an orphaned object is cleanable; a failed delete must not keep the record alive */ }
        return c.Id;
    }
}
