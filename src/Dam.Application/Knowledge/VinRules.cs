using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Knowledge;
using Dam.Shared.Identity;
using Dam.Shared.Knowledge;

namespace Dam.Application.Knowledge;

internal static class VinRuleAccess
{
    public static Task<VinRule?> FindAsync(IRepository<VinRule> rules, IQueryExecutor q, Guid id, CancellationToken ct) =>
        q.FirstOrDefaultAsync(rules.Query().Where(r => r.Id == id), ct);

    public static Task<bool> NameTakenAsync(IRepository<VinRule> rules, IQueryExecutor q, string name, Guid? except, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return q.AnyAsync(rules.Query().Where(r => r.Name.ToLower() == lower && (except == null || r.Id != except)), ct);
    }
}

// ------------------------------------------------------------------------------------------------------------- list

/// <summary>The tenant's VIN rules by name, paged by the lower-cased name. Whoever manages knowledge, or may edit assets, can read them.
/// <paramref name="Q"/> keeps rules whose name, pattern or model contains the text; <paramref name="Model"/> keeps one model.</summary>
public sealed record ListVinRulesQuery(string? Q, string? Model, string? Cursor, int? Limit) : IQuery<Page<VinRuleDto>>;

public sealed class ListVinRulesHandler(IRepository<VinRule> rules, IQueryExecutor q, IAuthorizationService authz) : IRequestHandler<ListVinRulesQuery, Page<VinRuleDto>>
{
    public async Task<Result<Page<VinRuleDto>>> Handle(ListVinRulesQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.KnowledgeManage, ct: ct) && !await authz.CanAsync(Permissions.AssetsUpdate, ct: ct)) return Error.Forbidden();
        var model = r.Model is { Length: > 0 } ? KnowledgeCodes.Normalize(r.Model) : null;
        if (r.Model is { Length: > 0 } && model is null) return Error.Validation(KnowledgeMapping.Fail("model", KnowledgeCodes.Hint("model")));

        var limit = Math.Clamp(r.Limit ?? 100, 1, 500);
        IQueryable<VinRule> query = rules.Query();
        if (model is not null) query = query.Where(x => x.Model == model);
        if (r.Q is { Length: > 0 } text)
        {
            var lower = text.Trim().ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(lower) || x.Pattern.ToLower().Contains(lower) || x.Model.Contains(lower));
        }
        if (!string.IsNullOrEmpty(r.Cursor)) { var after = r.Cursor.ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().CompareTo(after) > 0); }
        var rows = await q.ToListAsync(query.OrderBy(x => x.Name.ToLower()).Take(limit + 1), ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new Page<VinRuleDto>(rows.Select(KnowledgeMapping.ToDto).ToList(), more ? rows[^1].Name.ToLowerInvariant() : null);
    }
}

// ---------------------------------------------------------------------------------------------------------- create

public sealed record CreateVinRuleCommand(VinRuleInput Input) : ICommand<VinRuleDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class CreateVinRuleHandler(IRepository<VinRule> rules, IQueryExecutor q, ITenantContext tenant, IClock clock) : IRequestHandler<CreateVinRuleCommand, VinRuleDto>
{
    public async Task<Result<VinRuleDto>> Handle(CreateVinRuleCommand c, CancellationToken ct)
    {
        var errors = KnowledgeRules.ValidateRule(c.Input, out var facts, out var name);
        if (errors.Count == 0 && await VinRuleAccess.NameTakenAsync(rules, q, name, null, ct)) errors["name"] = ["A rule with this name already exists."];
        if (errors.Count == 0 && await q.CountAsync(rules.Query(), ct) >= KnowledgeLimits.MaxRulesPerTenant)
            errors["name"] = [string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A tenant holds at most {KnowledgeLimits.MaxRulesPerTenant:N0} VIN rules. Remove or merge rules before adding more.")];
        if (errors.Count > 0) return Error.Validation(errors);

        var rule = VinRule.Create(tenant.TenantId, name, facts!, clock.UtcNow);
        rules.Add(rule);
        return KnowledgeMapping.ToDto(rule);
    }
}

// ---------------------------------------------------------------------------------------------------------- update

public sealed record UpdateVinRuleCommand(Guid Id, VinRuleInput Input) : ICommand<VinRuleDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class UpdateVinRuleHandler(IRepository<VinRule> rules, IQueryExecutor q, IClock clock) : IRequestHandler<UpdateVinRuleCommand, VinRuleDto>
{
    public async Task<Result<VinRuleDto>> Handle(UpdateVinRuleCommand c, CancellationToken ct)
    {
        var found = await VinRuleAccess.FindAsync(rules, q, c.Id, ct);
        if (found is null) return Error.NotFound("VIN rule not found.");
        var errors = KnowledgeRules.ValidateRule(c.Input, out var facts, out var name);
        if (errors.Count == 0 && await VinRuleAccess.NameTakenAsync(rules, q, name, c.Id, ct)) errors["name"] = ["A rule with this name already exists."];
        if (errors.Count > 0) return Error.Validation(errors);

        found.Update(name, facts!, clock.UtcNow);
        return KnowledgeMapping.ToDto(found);
    }
}

// ---------------------------------------------------------------------------------------------------------- delete

public sealed record DeleteVinRuleCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class DeleteVinRuleHandler(IRepository<VinRule> rules, IQueryExecutor q) : IRequestHandler<DeleteVinRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteVinRuleCommand c, CancellationToken ct)
    {
        var found = await VinRuleAccess.FindAsync(rules, q, c.Id, ct);
        if (found is null) return Error.NotFound("VIN rule not found.");
        found.MarkDeleted();
        rules.Remove(found);
        return c.Id;
    }
}

// ------------------------------------------------------------------------------------------------------------- test

/// <summary>
/// Weighs every rule against a VIN without publishing or changing anything: which rule is chosen, the vehicle it describes, and for each rule why it matched or
/// not. A malformed VIN is not an error here; the answer says it is not valid and why.
/// </summary>
public sealed record TestVinQuery(string? Vin) : IQuery<VinTestDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class TestVinHandler(IRepository<VinRule> rules, IQueryExecutor q, IClock clock) : IRequestHandler<TestVinQuery, VinTestDto>
{
    public async Task<Result<VinTestDto>> Handle(TestVinQuery r, CancellationToken ct)
    {
        if (!Vin.TryNormalize(r.Vin, out var vin, out var why))
            return new VinTestDto(r.Vin?.Trim() ?? "", false, null, null, [], why);

        var views = (await q.ToListAsync(rules.Query(), ct)).Select(VinRuleView.From).OfType<VinRuleView>().ToList();
        var evaluated = VinRuleSelector.Evaluate(views, vin);
        var chosen = evaluated.FirstOrDefault(e => e.Matched)?.Rule;
        var candidates = evaluated.Select(e => new VinCandidateDto(e.Rule.Id, e.Rule.Name, e.Rule.Priority, e.Rule.Specificity, e.Matched, e.Reason)).ToList();
        if (chosen is null) return new VinTestDto(vin, true, null, null, candidates);
        var v = VinRuleSelector.VehicleFor(chosen, vin, clock.UtcNow.Year);
        return new VinTestDto(vin, true, new VinChosenDto(chosen.Id, chosen.Name), new VehicleDto(v.Model, v.Variant, v.ModelYear, v.Theme), candidates);
    }
}
