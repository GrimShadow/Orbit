using System.Globalization;
using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Ai;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Ai;

/// <summary>
/// The review queue (ADR-0036): assets with AI suggestions waiting for a person. Today that is translations. A translation review has no workflow instance, so it
/// is not a workflow task; the queue is every asset with pending translations that the caller may edit (<c>assets.update</c>, through the same role and
/// access-rule scope as editing, and readable), so nobody is shown an asset they could not open or decide on.
/// </summary>
public sealed class AiReviewReader(
    IRepository<AiSuggestion> suggestions, IRepository<Asset> assets, IRepository<Folder> folders, IAuthorizationService authz, IQueryExecutor q)
{
    /// <summary>The most pending suggestions looked at in one request; beyond that the oldest ones are served first and the queue is longer than it shows.</summary>
    public const int MaxRows = 5000;

    public sealed record Group(Guid AssetId, string Title, string AssetType, int Pending, IReadOnlyList<string> Languages, IReadOnlyList<string> Fields, DateTimeOffset OldestAt);

    /// <summary>The queue for the caller, oldest first (then by asset id).</summary>
    public async Task<IReadOnlyList<Group>> LoadAsync(CancellationToken ct)
    {
        var rows = await q.ToListAsync(
            suggestions.Query().Where(s => s.Kind == AiSuggestionKinds.Translation && s.Status == AiSuggestionStatuses.Pending)
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Take(MaxRows)
                .Select(s => new { s.AssetId, s.Field, s.Language, s.CreatedAt }), ct);
        if (rows.Count == 0) return [];

        var profile = await authz.GetProfileAsync(ct);
        var read = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        var update = PolicyEngine.GetScope(profile, Permissions.AssetsUpdate);
        if (read.Count == 0 || update.Count == 0) return [];

        var ids = rows.Select(r => r.AssetId).Distinct().ToList();
        var found = await q.ToListAsync(assets.Query().Where(a => ids.Contains(a.Id) && a.Status != AssetStatuses.Deleted && a.Status != AssetStatuses.Blocked), ct);
        var folderIds = found.Where(a => a.FolderId != null).Select(a => a.FolderId!.Value).Distinct().ToList();
        var paths = folderIds.Count == 0 ? [] : (await q.ToListAsync(folders.Query().Where(f => folderIds.Contains(f.Id)), ct)).ToDictionary(f => f.Id, f => f.Path);
        var allowed = found.Where(a =>
        {
            var resource = a.ToResource(a.FolderId is { } f && paths.TryGetValue(f, out var p) ? p : null);
            return read.Any(c => c.Matches(resource, profile.UserId)) && update.Any(c => c.Matches(resource, profile.UserId));
        }).ToDictionary(a => a.Id);

        return rows.Where(r => allowed.ContainsKey(r.AssetId)).GroupBy(r => r.AssetId)
            .Select(g => new Group(g.Key, allowed[g.Key].Title, allowed[g.Key].Type, g.Count(),
                g.Select(x => x.Language!).Distinct().Order(StringComparer.Ordinal).ToList(), g.Select(x => x.Field!).Distinct().Order(StringComparer.Ordinal).ToList(), g.Min(x => x.CreatedAt)))
            .OrderBy(x => x.OldestAt).ThenBy(x => x.AssetId).ToList();
    }
}

/// <summary>Assets with translations waiting for a decision that the caller may edit, oldest first. <paramref name="Kind"/> is translation (the default; nothing else is reviewable yet).</summary>
public sealed record ListAiReviewsQuery(string? Kind, string? Cursor, int? Limit) : IQuery<Page<AiReviewItemDto>>;

public sealed class ListAiReviewsValidator : AbstractValidator<ListAiReviewsQuery>
{
    public ListAiReviewsValidator() =>
        RuleFor(x => x.Kind).Must(k => k is null or AiSuggestionKinds.Translation).WithMessage("Kind must be: translation.").OverridePropertyName("kind");
}

public sealed class ListAiReviewsHandler(AiReviewReader reader, IRepository<TranslationJob> jobs, IQueryExecutor q) : IRequestHandler<ListAiReviewsQuery, Page<AiReviewItemDto>>
{
    public async Task<Result<Page<AiReviewItemDto>>> Handle(ListAiReviewsQuery r, CancellationToken ct)
    {
        var limit = Paging.Clamp(r.Limit);
        var all = await reader.LoadAsync(ct);
        IEnumerable<AiReviewReader.Group> after = all;
        if (ParseCursor(r.Cursor) is var (at, id)) after = all.Where(g => g.OldestAt.UtcTicks > at || (g.OldestAt.UtcTicks == at && g.AssetId.CompareTo(id) > 0));
        var page = after.Take(limit + 1).ToList();
        var more = page.Count > limit;
        if (more) page.RemoveAt(limit);

        var ids = page.Select(g => g.AssetId).ToList();
        var latest = ids.Count == 0 ? [] : (await q.ToListAsync(jobs.Query().Where(j => ids.Contains(j.AssetId)).OrderByDescending(j => j.Id).Select(j => new { j.AssetId, j.RequestedBy }), ct))
            .GroupBy(j => j.AssetId).ToDictionary(g => g.Key, g => g.First().RequestedBy);
        var items = page.Select(g => new AiReviewItemDto(g.AssetId, g.Title, g.AssetType, g.Pending, g.Languages, g.Fields, latest.GetValueOrDefault(g.AssetId), g.OldestAt)).ToList();
        return new Page<AiReviewItemDto>(items, more && page.Count > 0 ? Cursor(page[^1]) : null);
    }

    private static string Cursor(AiReviewReader.Group g) => $"{g.OldestAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{g.AssetId:N}";

    private static (long Ticks, Guid Id)? ParseCursor(string? cursor)
    {
        if (cursor is null) return null;
        var parts = cursor.Split('.');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParseExact(parts[1], "N", out var id) ? (ticks, id) : null;
    }
}

/// <summary>How many assets have translations waiting for the caller to review: the number on the Tasks badge. For any signed-in user (it counts only what they can edit).</summary>
public sealed record GetAiReviewSummaryQuery : IQuery<AiReviewSummaryDto>;

public sealed class GetAiReviewSummaryHandler(AiReviewReader reader) : IRequestHandler<GetAiReviewSummaryQuery, AiReviewSummaryDto>
{
    public async Task<Result<AiReviewSummaryDto>> Handle(GetAiReviewSummaryQuery r, CancellationToken ct) => new AiReviewSummaryDto((await reader.LoadAsync(ct)).Count);
}
