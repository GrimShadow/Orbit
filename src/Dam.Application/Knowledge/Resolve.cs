using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Knowledge;
using Dam.Shared.Knowledge;

namespace Dam.Application.Knowledge;

/// <summary>
/// "Which document describes this vehicle?" (ADR-0039 decision 5). Validates the VIN (422 when malformed), chooses the rule (404 when none matches), then
/// ranks the documents of the rule's model and type that the caller may read, by exactly the rules assets themselves use: RBAC, ABAC and status visibility.
/// A document the caller cannot read is invisible, never mentioned. Reads only, so it writes and audits nothing.
/// </summary>
public sealed record ResolveKnowledgeQuery(string? Vin, string? Type, string? Lang) : IQuery<ResolveDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ResolveKnowledgeHandler(
    IRepository<VinRule> rules, IRepository<KnowledgeDocument> documents, IRepository<Asset> assets, IRepository<AssetVersion> versions, IRepository<Domain.Content.Folder> folders,
    KnowledgeReader reader, IQueryExecutor q, IAuthorizationService authz, IClock clock) : IRequestHandler<ResolveKnowledgeQuery, ResolveDto>
{
    /// <summary>How many documents of one model are weighed. A model with more documents than this is a data problem, not a lookup problem.</summary>
    private const int MaxCandidates = 2000;

    /// <summary>Stable machine codes on the problem answers, so a client need not parse the English message.</summary>
    public const string InvalidVin = "knowledge.invalid_vin";
    public const string NoRule = "knowledge.no_rule";
    public const string NoDocument = "knowledge.no_document";

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };

    private sealed class Joined
    {
        public required KnowledgeDocument D { get; init; }
        public required Asset A { get; init; }
    }

    public async Task<Result<ResolveDto>> Handle(ResolveKnowledgeQuery r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var vinOk = Vin.TryNormalize(r.Vin, out var vin, out var vinError);
        if (!vinOk) errors["vin"] = [vinError];
        var type = string.IsNullOrWhiteSpace(r.Type) ? KnowledgeDocTypes.OwnerManual : r.Type.Trim().ToLowerInvariant();
        if (!KnowledgeDocTypes.IsKnown(type)) errors["type"] = [$"type must be one of: {string.Join(", ", KnowledgeDocTypes.All)}."];
        if (errors.Count > 0)
            return vinOk
                ? Error.Validation(errors)
                : new Error("validation_failed", "One or more validation errors occurred.", ErrorType.Validation, errors, Code(InvalidVin));

        var views = (await q.ToListAsync(rules.Query().Where(x => x.Enabled), ct)).Select(VinRuleView.From).OfType<VinRuleView>().ToList();
        var rule = VinRuleSelector.Choose(views, vin);
        if (rule is null) return new Error("not_found", "No rule matches this VIN.", ErrorType.NotFound, null, Code(NoRule));
        var vehicle = VinRuleSelector.VehicleFor(rule, vin, clock.UtcNow.Year);

        var model = rule.Model;
        var variant = rule.Variant;
        var query =
            from d in documents.Query()
            join a in assets.Query() on d.AssetId equals a.Id
            where d.DocType == type && d.Model == model && (d.Variant == null || d.Variant == variant)
                  && (a.Status == AssetStatuses.Published || a.Status == AssetStatuses.InReview)
            select new Joined { D = d, A = a };
        var found = await q.ToListAsync(query.OrderBy(x => x.D.Id).Take(MaxCandidates), ct);

        // Only documents the caller may read are weighed, so ranking can never reveal one that is hidden from them.
        var profile = await authz.GetProfileAsync(ct);
        var folderIds = found.Where(x => x.A.FolderId != null).Select(x => x.A.FolderId!.Value).Distinct().ToList();
        var pathById = folderIds.Count == 0
            ? []
            : (await q.ToListAsync(folders.Query().Where(f => folderIds.Contains(f.Id)).Select(f => new { f.Id, f.Path }), ct)).ToDictionary(f => f.Id, f => f.Path);
        var readable = found.Where(x => PolicyEngine.Can(profile, Permissions.AssetsRead, x.A.ToResource(x.A.FolderId is { } f && pathById.TryGetValue(f, out var p) ? p : null))).ToList();

        var candidates = readable.Select(x => new DocumentCandidate(
            x.D.Id, x.D.AssetId, x.D.Variant, x.D.ModelYearFrom, x.D.ModelYearTo, x.D.Language, x.A.Status,
            x.D.UpdatedAt > x.A.UpdatedAt ? x.D.UpdatedAt : x.A.UpdatedAt)).ToList();

        var languages = await reader.LanguagesAsync(ct);
        var selection = DocumentRanking.Select(candidates, vehicle.Variant, vehicle.ModelYear, r.Lang?.Trim().ToLowerInvariant(), languages.Default);
        if (selection is null)
            return new Error("not_found", $"No {type.Replace('_', ' ')} is available for this vehicle.", ErrorType.NotFound, null,
                new Dictionary<string, object?>
                {
                    ["code"] = NoDocument,
                    ["vehicle"] = new VehicleDto(vehicle.Model, vehicle.Variant, vehicle.ModelYear, vehicle.Theme),
                });

        var chosen = readable.First(x => x.D.Id == selection.Chosen.DocumentId);
        int? versionNumber = chosen.A.CurrentVersionId is { } vid
            ? await q.FirstOrDefaultAsync(versions.Query().Where(v => v.Id == vid).Select(v => (int?)v.VersionNo), ct)
            : null;

        return new ResolveDto(
            vin,
            new VinRuleRefDto(rule.Id, rule.Name, rule.Priority),
            new VehicleDto(vehicle.Model, vehicle.Variant, vehicle.ModelYear, vehicle.Theme ?? chosen.D.Theme),
            KnowledgeMapping.ToResolved(chosen.D, chosen.A, versionNumber),
            selection.OtherLanguages.Select(o => new OtherLanguageDto(o.Language, o.DocumentId, o.AssetId)).ToList());
    }
}
