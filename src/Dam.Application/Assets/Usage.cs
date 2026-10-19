using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

/// <summary>
/// Records that assets were used (ADR-0029): console downloads and copy-links, a player's proof of play, a portal or an app.
/// Any user may record events, but only for assets they can read; an asset that does not exist and one the caller may not
/// read are counted the same way (<c>rejected</c>) so the answer never reveals which assets exist.
/// <para>Events are append-only facts and can arrive in the thousands per minute, so unlike other writes they do not raise a
/// domain event per row: that would put every one through the hash-chained audit log. The table is itself the record.</para>
/// </summary>
public sealed record RecordUsageEventsCommand(IReadOnlyList<UsageEventInput> Events) : ICommand<RecordUsageResultDto>;

public sealed class RecordUsageEventsValidator : AbstractValidator<RecordUsageEventsCommand>
{
    public RecordUsageEventsValidator(IClock clock)
    {
        RuleFor(x => x.Events).NotNull().Must(e => e is { Count: >= 1 and <= UsageRules.MaxBatch })
            .WithMessage($"Send between 1 and {UsageRules.MaxBatch} events.");
        RuleForEach(x => x.Events).ChildRules(e =>
        {
            e.RuleFor(x => x.AssetId).NotEmpty();
            e.RuleFor(x => x.EventType).Must(UsageEventTypes.IsKnown).WithMessage($"Must be one of: {string.Join(", ", UsageEventTypes.All)}.");
            e.RuleFor(x => x.ClientEventId).MaximumLength(UsageRules.MaxClientEventIdLength);
            e.RuleFor(x => x.Channel).MaximumLength(UsageRules.MaxChannelLength);
            e.RuleFor(x => x.Rendition).MaximumLength(UsageRules.MaxRenditionLength);
            e.RuleFor(x => x.OccurredAt).Custom((at, ctx) =>
            {
                UsageRules.ResolveOccurredAt(at, clock.UtcNow, out var error);
                if (error is not null) ctx.AddFailure(error);
            });
            e.RuleFor(x => x.Context).Custom((context, ctx) =>
            {
                if (UsageRules.ContextError(context) is { } error) ctx.AddFailure(error);
            });
        });
    }
}

public sealed class RecordUsageEventsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IUsageStore store,
    ITenantContext tenant, IClock clock) : IRequestHandler<RecordUsageEventsCommand, RecordUsageResultDto>
{
    public async Task<Result<RecordUsageResultDto>> Handle(RecordUsageEventsCommand c, CancellationToken ct)
    {
        var profile = await authz.GetProfileAsync(ct);
        var wanted = c.Events.Select(e => e.AssetId).Distinct().ToList();
        var found = await q.ToListAsync(assets.Query().Where(a => wanted.Contains(a.Id)), ct);

        var folderIds = found.Where(a => a.FolderId != null).Select(a => a.FolderId!.Value).Distinct().ToList();
        var pathById = folderIds.Count == 0
            ? []
            : (await q.ToListAsync(folders.Query().Where(f => folderIds.Contains(f.Id)).Select(f => new { f.Id, f.Path }), ct)).ToDictionary(f => f.Id, f => f.Path);

        var readable = found
            .Where(a => PolicyEngine.Can(profile, Permissions.AssetsRead, a.ToResource(a.FolderId is { } f && pathById.TryGetValue(f, out var p) ? p : null)))
            .Select(a => a.Id).ToHashSet();

        var now = clock.UtcNow;
        var events = new List<UsageEvent>();
        var rejected = 0;
        foreach (var e in c.Events)
        {
            if (!readable.Contains(e.AssetId)) { rejected++; continue; }
            events.Add(UsageEvent.Create(
                tenant.TenantId, e.AssetId, e.EventType, e.Channel, e.Rendition, profile.UserId,
                UsageRules.ResolveOccurredAt(e.OccurredAt, now, out _)!.Value, e.ClientEventId, ContextOf(e.Context), now));
        }

        var stored = events.Count == 0 ? 0 : await store.AppendAsync(events, ct);
        return new RecordUsageResultDto(stored, events.Count - stored, rejected);
    }

    private static Dictionary<string, JsonElement>? ContextOf(JsonElement? context)
    {
        if (context is not { ValueKind: JsonValueKind.Object } o) return null;
        var d = new Dictionary<string, JsonElement>();
        foreach (var p in o.EnumerateObject()) d[p.Name] = p.Value.Clone(); // a repeated key keeps the last value
        return d;
    }
}
