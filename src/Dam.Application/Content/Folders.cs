using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Application.Identity;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Content;

public sealed record ListFoldersQuery : IQuery<IReadOnlyList<FolderDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ListFoldersHandler(IRepository<Folder> folders, IQueryExecutor q) : IRequestHandler<ListFoldersQuery, IReadOnlyList<FolderDto>>
{
    public async Task<Result<IReadOnlyList<FolderDto>>> Handle(ListFoldersQuery r, CancellationToken ct) =>
        (await q.ToListAsync(folders.Query().OrderBy(f => f.Path), ct)).Select(ContentMapping.ToDto).ToList();
}

public sealed record GetFolderTreeQuery : IQuery<IReadOnlyList<FolderNodeDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetFolderTreeHandler(IRepository<Folder> folders, IQueryExecutor q) : IRequestHandler<GetFolderTreeQuery, IReadOnlyList<FolderNodeDto>>
{
    public async Task<Result<IReadOnlyList<FolderNodeDto>>> Handle(GetFolderTreeQuery r, CancellationToken ct)
    {
        var all = await q.ToListAsync(folders.Query(), ct);
        return TreeBuilder.Build<Folder, FolderNodeDto>(all, f => f.Id, f => f.ParentId,
            (f, kids) => new FolderNodeDto(ContentMapping.ToDto(f), kids), (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record GetFolderQuery(Guid Id) : IQuery<FolderDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetFolderHandler(IRepository<Folder> folders, IQueryExecutor q) : IRequestHandler<GetFolderQuery, FolderDetailDto>
{
    public async Task<Result<FolderDetailDto>> Handle(GetFolderQuery r, CancellationToken ct)
    {
        var f = await q.FirstOrDefaultAsync(folders.Query().Where(x => x.Id == r.Id), ct);
        if (f is null) return Error.NotFound("Folder not found.");

        // Ancestors are the folders whose paths are the proper prefixes of this path.
        var labels = f.Path.Split('.');
        var prefixes = Enumerable.Range(1, labels.Length - 1).Select(n => string.Join('.', labels.Take(n))).ToList();
        var ancestors = (await q.ToListAsync(folders.Query().Where(x => prefixes.Contains(x.Path)), ct)).OrderBy(x => x.Path.Length).ToList();

        var effective = new Dictionary<string, JsonElement>();
        foreach (var a in ancestors.Append(f)) foreach (var (k, v) in a.InheritedMetadata) effective[k] = v; // deeper wins
        var children = await q.CountAsync(folders.Query().Where(x => x.ParentId == f.Id), ct);
        return new FolderDetailDto(ContentMapping.ToDto(f), ancestors.Select(a => new FolderRefDto(a.Id, a.Path, a.Name)).ToList(), effective, children);
    }
}

/// <summary>Creates a folder. Permission is checked per parent folder, so a rule scoped to a subtree lets its holder build inside it.</summary>
public sealed record CreateFolderCommand(Guid? ParentId, string Name, string? Label, JsonElement? InheritedMetadata) : ICommand<Guid>;

public sealed class CreateFolderValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Label).Must(l => l is null || Paths.IsValidLabel(l)).WithMessage("Use lowercase letters, digits and underscores (max 64).");
    }
}

public sealed class CreateFolderHandler(
    IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, ITenantContext tenant, ICurrentUser me, IClock clock, IEventCollector events)
    : IRequestHandler<CreateFolderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateFolderCommand c, CancellationToken ct)
    {
        Folder? parent = null;
        if (c.ParentId is { } pid)
        {
            parent = await q.FirstOrDefaultAsync(folders.Query().Where(f => f.Id == pid), ct);
            if (parent is null) return Error.Validation(ContentMapping.Fail("parentId", "Parent folder not found."));
            if (Paths.Depth(parent.Path) >= Paths.MaxDepth) return Error.Validation(ContentMapping.Fail("parentId", $"Folders can be nested at most {Paths.MaxDepth} deep."));
        }
        if (!await FolderGuard.CanAsync(authz, Permissions.FoldersManage, parent?.Path, ct)) return Error.Forbidden();
        if (!ContentMapping.TryObject(c.InheritedMetadata, out var inherited, out var err)) return Error.Validation(ContentMapping.Fail("inheritedMetadata", err!));

        var siblings = (await q.ToListAsync(folders.Query().Where(f => f.ParentId == c.ParentId), ct)).Select(f => f.Label).ToHashSet();
        string label;
        if (c.Label is not null)
        {
            if (siblings.Contains(c.Label)) return Error.Conflict($"A folder labelled '{c.Label}' already exists here.");
            label = c.Label;
        }
        else label = Paths.Unique(Paths.Slugify(c.Name, "folder"), siblings);

        var folder = Folder.Create(tenant.TenantId, parent, c.Name, label, inherited, me.UserId, clock.UtcNow);
        folders.Add(folder);
        events.Raise(Events.Changed("folder.created", "folder", folder.Id));
        return folder.Id;
    }
}

public sealed record UpdateFolderCommand(Guid Id, string? Name, JsonElement? InheritedMetadata, Guid? DefaultWorkflowId, bool ClearDefaultWorkflow = false)
    : ICommand<Guid>;

public sealed class UpdateFolderValidator : AbstractValidator<UpdateFolderCommand>
{
    public UpdateFolderValidator() => RuleFor(x => x.Name).MaximumLength(200).When(x => x.Name is not null);
}

public sealed class UpdateFolderHandler(IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IEventCollector events)
    : IRequestHandler<UpdateFolderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateFolderCommand c, CancellationToken ct)
    {
        var f = await q.FirstOrDefaultAsync(folders.Query().Where(x => x.Id == c.Id), ct);
        if (f is null) return Error.NotFound("Folder not found.");
        if (!await FolderGuard.CanAsync(authz, Permissions.FoldersManage, f.Path, ct)) return Error.Forbidden();
        if (c.Name is { Length: 0 }) return Error.Validation(ContentMapping.Fail("name", "Name cannot be empty."));
        if (!ContentMapping.TryObject(c.InheritedMetadata, out var inherited, out var err)) return Error.Validation(ContentMapping.Fail("inheritedMetadata", err!));

        if (!string.IsNullOrWhiteSpace(c.Name)) f.Rename(c.Name);
        if (c.InheritedMetadata is not null) f.SetInheritedMetadata(inherited);
        if (c.ClearDefaultWorkflow) f.SetDefaultWorkflow(null); else if (c.DefaultWorkflowId is not null) f.SetDefaultWorkflow(c.DefaultWorkflowId);
        events.Raise(Events.Changed("folder.updated", "folder", f.Id));
        return f.Id;
    }
}

public sealed record MoveFolderCommand(Guid Id, Guid? NewParentId) : ICommand<Guid>;

public sealed class MoveFolderHandler(IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IEventCollector events)
    : IRequestHandler<MoveFolderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(MoveFolderCommand c, CancellationToken ct)
    {
        var f = await q.FirstOrDefaultAsync(folders.Query().Where(x => x.Id == c.Id), ct);
        if (f is null) return Error.NotFound("Folder not found.");
        Folder? target = null;
        if (c.NewParentId is { } tid)
        {
            target = await q.FirstOrDefaultAsync(folders.Query().Where(x => x.Id == tid), ct);
            if (target is null) return Error.Validation(ContentMapping.Fail("newParentId", "Destination folder not found."));
            if (Paths.IsSelfOrDescendant(f.Path, target.Path)) return Error.Conflict("A folder cannot be moved into itself or one of its own sub-folders.");
        }
        // Moving needs authority over both ends: where it comes from and where it goes.
        if (!await FolderGuard.CanAsync(authz, Permissions.FoldersManage, f.Path, ct)
            || !await FolderGuard.CanAsync(authz, Permissions.FoldersManage, target?.Path, ct)) return Error.Forbidden();
        if (f.ParentId == c.NewParentId) return f.Id;

        var siblings = await q.ToListAsync(folders.Query().Where(x => x.ParentId == c.NewParentId), ct);
        if (siblings.Any(s => s.Label == f.Label)) return Error.Conflict($"The destination already has a folder labelled '{f.Label}'.");

        var oldPrefix = f.Path;
        var descendants = await q.ToListAsync(folders.Query().Where(x => x.Path.StartsWith(oldPrefix + ".")), ct);
        f.MoveTo(target);
        var deepest = Paths.Depth(f.Path) + (descendants.Count == 0 ? 0 : descendants.Max(d => Paths.Depth(d.Path) - Paths.Depth(oldPrefix)));
        if (deepest > Paths.MaxDepth) return Error.Validation(ContentMapping.Fail("newParentId", $"The move would nest folders deeper than {Paths.MaxDepth} levels."));
        foreach (var d in descendants) d.RewritePath(oldPrefix, f.Path);

        events.Raise(Events.Changed("folder.moved", "folder", f.Id));
        return f.Id;
    }
}

public sealed record DeleteFolderCommand(Guid Id) : ICommand<Guid>;

public sealed class DeleteFolderHandler(IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IEventCollector events)
    : IRequestHandler<DeleteFolderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteFolderCommand c, CancellationToken ct)
    {
        var f = await q.FirstOrDefaultAsync(folders.Query().Where(x => x.Id == c.Id), ct);
        if (f is null) return Error.NotFound("Folder not found.");
        if (!await FolderGuard.CanAsync(authz, Permissions.FoldersManage, f.Path, ct)) return Error.Forbidden();
        if (await q.AnyAsync(folders.Query().Where(x => x.ParentId == f.Id), ct)) return Error.Conflict("The folder still has sub-folders. Delete or move them first.");
        // Assets arrive in Step 1.3, which adds the "folder still holds assets" check here.
        folders.Remove(f);
        events.Raise(Events.Changed("folder.deleted", "folder", f.Id));
        return f.Id;
    }
}
