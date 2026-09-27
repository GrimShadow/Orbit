using Dam.Application.Assets;
using Dam.Application.Messaging;
using Dam.Shared.Content;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

public sealed record RetentionPolicyRequest(string Name, int? ArchiveAfterDays, int? DeleteAfterDays, bool LegalHoldExempt = false);

/// <summary>Admin CRUD for retention policies (spec Admin area `/retention-policies`). Assigning a policy to an asset is a
/// separate, per-asset action: <c>POST /assets/{id}/retention-policy</c>.</summary>
public static class RetentionEndpoints
{
    public static void MapRetentionEndpoints(this RouteGroupBuilder v1)
    {
        var policies = v1.MapGroup("/retention-policies").WithTags("Retention");

        policies.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListRetentionPoliciesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListRetentionPolicies").Produces<IReadOnlyList<RetentionPolicyDto>>();
        policies.MapPost("", (RetentionPolicyRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateRetentionPolicyCommand(r.Name, r.ArchiveAfterDays, r.DeleteAfterDays, r.LegalHoldExempt),
                    v => Task.FromResult(Results.Created($"/api/v1/retention-policies/{v.Id}", v)), ct))
            .WithName("CreateRetentionPolicy").Produces<RetentionPolicyDto>(StatusCodes.Status201Created);
        policies.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetRetentionPolicyQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetRetentionPolicy").Produces<RetentionPolicyDto>();
        policies.MapPut("/{id:guid}", (Guid id, RetentionPolicyRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateRetentionPolicyCommand(id, r.Name, r.ArchiveAfterDays, r.DeleteAfterDays, r.LegalHoldExempt), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateRetentionPolicy").Produces<RetentionPolicyDto>();
        policies.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteRetentionPolicyCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteRetentionPolicy").WithSummary("Refused while the policy is still assigned to any asset");
    }
}
