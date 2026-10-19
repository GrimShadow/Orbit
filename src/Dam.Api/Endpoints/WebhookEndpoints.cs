using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Application.Webhooks;
using Dam.Shared.Identity;
using Dam.Shared.Webhooks;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>Outbound webhooks, their delivery log, replay and ping (webhooks.manage). ADR-0031.</summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this RouteGroupBuilder v1)
    {
        var hooks = v1.MapGroup("/webhooks").WithTags("Webhooks");

        hooks.MapGet("/events", (IDispatcher d, CancellationToken ct) => Run(d, new ListWebhookEventTypesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListWebhookEventTypes").WithSummary("The events webhooks and chat channels can subscribe to. Needs webhooks.manage or notifications.manage")
            .Produces<IReadOnlyList<WebhookEventTypeDto>>();
        hooks.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListWebhooksQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListWebhooks").WithSummary("Webhooks with their last-24-hour delivery counts. Needs webhooks.manage").Produces<IReadOnlyList<WebhookDto>>();
        hooks.MapPost("", (WebhookInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateWebhookCommand(r.Name, r.Url, r.Events ?? [], r.Active), async created =>
                {
                    var hook = await d.Send(new GetWebhookQuery(created.Id), ct);
                    return hook.IsFailure ? hook.Error.ToProblem() : Results.Created($"/api/v1/webhooks/{created.Id}", new WebhookCreatedDto(hook.Value, created.Secret));
                }, ct))
            .WithName("CreateWebhook")
            .WithSummary("Creates a webhook. The signing secret is in this response only (whsec_...); keep it. Private and internal addresses are refused")
            .Produces<WebhookCreatedDto>(StatusCodes.Status201Created);
        hooks.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetWebhookQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetWebhook").Produces<WebhookDto>();
        hooks.MapPut("/{id:guid}", (Guid id, WebhookInput r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateWebhookCommand(id, r.Name, r.Url, r.Events ?? [], r.Active), wid => new GetWebhookQuery(wid), ct))
            .WithName("UpdateWebhook").WithSummary("Replaces a webhook's name, URL, events and active flag (the secret is unchanged)").Produces<WebhookDto>();
        hooks.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new DeleteWebhookCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteWebhook").WithSummary("Deletes a webhook and its delivery history");
        hooks.MapPost("/{id:guid}/rotate-secret", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new RotateWebhookSecretCommand(id), async rotated =>
                {
                    var hook = await d.Send(new GetWebhookQuery(rotated.Id), ct);
                    return hook.IsFailure ? hook.Error.ToProblem() : Results.Ok(new WebhookSecretDto(hook.Value, rotated.Secret));
                }, ct))
            .WithName("RotateWebhookSecret").WithSummary("Generates a new signing secret, returned once; the old one stops working at once").Produces<WebhookSecretDto>();
        hooks.MapPost("/{id:guid}/ping", (Guid id, IDispatcher d, IWebhookDeliverer deliverer, CancellationToken ct) =>
                Run(d, new PingWebhookCommand(id), deliveryId => AttemptAndReadAsync(d, deliverer, deliveryId, ct), ct))
            .WithName("PingWebhook").WithSummary("Sends a synthetic webhook.ping event now and returns the delivery with its outcome (409 if the webhook is switched off)")
            .Produces<WebhookDeliveryDetailDto>();
        hooks.MapGet("/{id:guid}/deliveries", (Guid id, IDispatcher d, CancellationToken ct, string? status, string? eventType, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit) =>
                Run(d, new ListWebhookDeliveriesQuery(id, status, eventType, from, to, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListWebhookDeliveries")
            .WithSummary("One webhook's deliveries, newest first. Filter by status (pending, succeeded, failed), eventType (exact, or a prefix such as workflow.*) and created time (from, to)")
            .Produces<Page<WebhookDeliveryDto>>();

        var log = v1.MapGroup("/webhook-deliveries").WithTags("Webhooks");

        log.MapGet("", (IDispatcher d, CancellationToken ct, Guid? webhookId, string? status, string? eventType, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit) =>
                Run(d, new ListWebhookDeliveriesQuery(webhookId, status, eventType, from, to, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAllWebhookDeliveries").WithSummary("The delivery log across all webhooks (same filters as a single webhook's, plus webhookId)").Produces<Page<WebhookDeliveryDto>>();
        log.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetWebhookDeliveryQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetWebhookDelivery").WithSummary("One delivery with the exact payload sent and the receiver's response excerpt").Produces<WebhookDeliveryDetailDto>();
        log.MapPost("/{id:guid}/replay", (Guid id, IDispatcher d, IWebhookDeliverer deliverer, CancellationToken ct) =>
                Run(d, new ReplayWebhookDeliveryCommand(id), deliveryId => AttemptAndReadAsync(d, deliverer, deliveryId, ct), ct))
            .WithName("ReplayWebhookDelivery")
            .WithSummary("Creates a new delivery of the same payload (new id, replayOf set), attempts it now and returns it with its outcome")
            .Produces<WebhookDeliveryDetailDto>();
    }

    /// <summary>The delivery was committed by its command; make the first attempt now (within this request) and answer with the result.</summary>
    private static async Task<IResult> AttemptAndReadAsync(IDispatcher d, IWebhookDeliverer deliverer, Guid deliveryId, CancellationToken ct)
    {
        await deliverer.AttemptAsync(deliveryId, ct);
        var read = await d.Send(new GetWebhookDeliveryQuery(deliveryId), ct);
        return read.IsSuccess ? Results.Ok(read.Value) : read.Error.ToProblem();
    }
}
