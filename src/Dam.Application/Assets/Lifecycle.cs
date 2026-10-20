using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Licensing;
using Dam.Application.Messaging;
using Dam.Application.Workflow;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

/// <summary>
/// Moves an asset along its lifecycle (spec A4.2): submit, approve, reject, publish, withdraw, expire, archive. The state
/// machine itself (which actions are legal from which status) lives in <see cref="AssetLifecycle"/>; this handler adds the
/// guards the spec calls for on top: permission, required metadata complete (submit, publish) and workflow approved
/// (publish) and rights valid (publish; ADR-0033): an asset whose licence has ended or not yet begun cannot be published.
/// <para>When a workflow applies to the asset (ADR-0030), submit also starts it and approving or rejecting is done through its
/// tasks instead of directly; withdrawing cancels it. When none applies, nothing about the transitions changes.</para>
/// </summary>
public sealed record TransitionAssetCommand(Guid AssetId, string Action, string? Reason) : ICommand<AssetDto>;

public sealed class TransitionAssetValidator : AbstractValidator<TransitionAssetCommand>
{
    public TransitionAssetValidator()
    {
        RuleFor(x => x.Action).Must(AssetLifecycle.Actions.Contains).WithMessage($"Action must be one of: {string.Join(", ", AssetLifecycle.Actions)}.");
        RuleFor(x => x.Reason).NotEmpty().When(x => x.Action == AssetLifecycle.Reject).WithMessage("A reason is required to reject an asset.");
    }
}

public sealed class TransitionAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz,
    AssetMetadataBuilder builder, IWorkflowGate workflow, WorkflowRuntime runtime, RightsGuard rightsGuard, IClock clock)
    : IRequestHandler<TransitionAssetCommand, AssetDto>
{
    // The response is built from the asset just transitioned, not read back afterwards: some actions (e.g. approve) move
    // the asset to a status the acting user cannot see (an Approver scoped to "in_review" cannot see "approved"), and
    // re-reading would then report "not found" for a mutation that plainly succeeded (spec A7.2, same reasoning as create).
    public async Task<Result<AssetDto>> Handle(TransitionAssetCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!AssetLifecycle.TryGetTarget(asset.Status, c.Action, out var to))
            return Error.Conflict($"Cannot {c.Action} an asset that is {asset.Status.Replace('_', ' ')}.");
        if (!await authz.CanAsync(Permission(c.Action), asset.ToResource(path), ct)) return Error.Forbidden();
        if (c.Action is AssetLifecycle.Approve or AssetLifecycle.Reject && await runtime.HasRunningAsync(asset.Id, ct))
            return Error.Conflict("This asset is going through a workflow. Decide via the workflow task instead.");

        if (c.Action is AssetLifecycle.Submit or AssetLifecycle.Publish)
        {
            var built = await builder.BuildAsync(asset.Type, null, JsonSerializer.SerializeToElement(asset.Metadata), requireFields: true, ct);
            if (built.IsFailure) return built.Error;
        }
        if (c.Action == AssetLifecycle.Publish && !await workflow.IsApprovedAsync(asset.Id, ct))
            return Error.Conflict("This asset has not completed its approval workflow.");

        if (c.Action == AssetLifecycle.Publish && RightsGuard.OutOfForceMessage(await rightsGuard.CheckAsync(asset, null, null, ct)) is { } outOfForce)
            return Error.Conflict($"{outOfForce} Renew the licence or assign other rights before publishing.");

        var reason = c.Reason ?? DefaultReason(c.Action);
        if (c.Action == AssetLifecycle.Submit)
        {
            var started = await runtime.StartIfBoundAsync(asset, path, (await authz.GetProfileAsync(ct)).UserId, ct);
            if (started.IsFailure) return started.Error;
            if (started.Value is { } workflowName && c.Reason is null) reason = $"Submitted for review (workflow '{workflowName}').";
        }
        else if (c.Action == AssetLifecycle.Withdraw && asset.Status == AssetStatuses.InReview)
            await runtime.CancelRunningAsync(asset.Id, AssetStatuses.Draft, c.Reason ?? "Withdrawn.", ct);

        asset.ChangeStatus(to, reason, clock.UtcNow);
        return AssetMapping.ToDto(asset);
    }

    /// <summary>Approving and rejecting are deliberately scoped-only powers (ADR-0002: no role grants them tenant-wide; an
    /// access rule assigns the folders a reviewer may decide in), so both read from the same permission as the built-in
    /// roles never carry it directly.</summary>
    private static string Permission(string action) => action switch
    {
        AssetLifecycle.Submit => Permissions.AssetsSubmit,
        AssetLifecycle.Approve or AssetLifecycle.Reject => Permissions.AssetsApprove,
        AssetLifecycle.Publish or AssetLifecycle.Expire => Permissions.AssetsPublish,
        AssetLifecycle.Withdraw => Permissions.AssetsWithdraw,
        AssetLifecycle.Archive => Permissions.RetentionManage,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown transition action."),
    };

    private static string DefaultReason(string action) => action switch
    {
        AssetLifecycle.Submit => "Submitted for review.",
        AssetLifecycle.Approve => "Approved.",
        AssetLifecycle.Reject => "Rejected.",
        AssetLifecycle.Publish => "Published.",
        AssetLifecycle.Withdraw => "Withdrawn.",
        AssetLifecycle.Expire => "Expired.",
        AssetLifecycle.Archive => "Archived.",
        _ => action,
    };
}
