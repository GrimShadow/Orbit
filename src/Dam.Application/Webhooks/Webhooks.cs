using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Webhooks;
using Dam.Shared.Identity;
using Dam.Shared.Webhooks;
using FluentValidation;

namespace Dam.Application.Webhooks;

internal static class WebhookRules
{
    public const int MaxPerTenant = 100;

    public static void ValidEvents<T>(this IRuleBuilder<T, IReadOnlyList<string>> rule) =>
        rule.NotNull().Must(e => e is { Count: > 0 }).WithMessage("Choose at least one event.")
            .Must(e => e is null || e.Count <= 50).WithMessage("At most 50 events.")
            .Custom((events, ctx) =>
            {
                for (var i = 0; i < (events?.Count ?? 0); i++)
                {
                    var p = events![i].Trim().ToLowerInvariant();
                    if (!EventPattern.IsValid(p)) ctx.AddFailure($"{ctx.PropertyPath}[{i}]", $"'{events[i]}' is not an event name or pattern (use e.g. asset.published, asset.* or *).");
                    else if (!EventCatalogue.All.Any(e => EventPattern.Matches(p, e.Name))) ctx.AddFailure($"{ctx.PropertyPath}[{i}]", $"'{events[i]}' matches no known event.");
                }
            });
}

/// <summary>Turns webhooks into DTOs, with their recent delivery statistics.</summary>
public sealed class WebhookMapper(IRepository<WebhookDelivery> deliveries, IQueryExecutor q, IClock clock)
{
    public async Task<List<WebhookDto>> ToDtosAsync(IReadOnlyList<Webhook> hooks, CancellationToken ct)
    {
        if (hooks.Count == 0) return [];
        var ids = hooks.Select(h => h.Id).ToList();
        var since = clock.UtcNow.AddHours(-24);
        var recent = await q.ToListAsync(deliveries.Query().Where(d => ids.Contains(d.WebhookId) && (d.CreatedAt >= since || d.Status == DeliveryStatuses.Pending))
            .Select(d => new { d.WebhookId, d.Status, d.CreatedAt }), ct);
        var latest = (await q.ToListAsync(deliveries.Query().Where(d => ids.Contains(d.WebhookId)).GroupBy(d => d.WebhookId)
            .Select(g => new { WebhookId = g.Key, At = g.Max(x => x.CreatedAt) }), ct)).ToDictionary(x => x.WebhookId, x => x.At);
        var lastStatus = new Dictionary<Guid, string>();
        foreach (var (id, at) in latest)
        {
            var status = await q.FirstOrDefaultAsync(deliveries.Query().Where(d => d.WebhookId == id && d.CreatedAt == at).Select(d => d.Status), ct);
            if (status is not null) lastStatus[id] = status;
        }
        return hooks.Select(h =>
        {
            var mine = recent.Where(r => r.WebhookId == h.Id).ToList();
            var stats = new WebhookStatsDto(
                mine.Count(r => r.Status == DeliveryStatuses.Succeeded && r.CreatedAt >= since),
                mine.Count(r => r.Status == DeliveryStatuses.Failed && r.CreatedAt >= since),
                mine.Count(r => r.Status == DeliveryStatuses.Pending),
                latest.TryGetValue(h.Id, out var l) ? l : null, lastStatus.GetValueOrDefault(h.Id));
            return ToDto(h, stats);
        }).ToList();
    }

    public static WebhookDto ToDto(Webhook h, WebhookStatsDto stats) =>
        new(h.Id, h.Name, h.Url, h.SecretLast4, h.Events, h.Active, h.CreatedBy, h.CreatedAt, h.UpdatedAt, stats);
}

public sealed class DeliveryMapper(IRepository<Webhook> webhooks, IQueryExecutor q, WebhookSettings settings)
{
    public async Task<List<WebhookDeliveryDto>> ToDtosAsync(IReadOnlyList<WebhookDelivery> list, CancellationToken ct)
    {
        var names = await NamesAsync(list, ct);
        return list.Select(d => ToDto(d, names.GetValueOrDefault(d.WebhookId) ?? "", settings.Retry.MaxAttempts)).ToList();
    }

    public async Task<WebhookDeliveryDetailDto> ToDetailAsync(WebhookDelivery d, CancellationToken ct)
    {
        var names = await NamesAsync([d], ct);
        return ToDetail(d, names.GetValueOrDefault(d.WebhookId) ?? "", settings.Retry.MaxAttempts);
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IReadOnlyList<WebhookDelivery> list, CancellationToken ct)
    {
        var ids = list.Select(d => d.WebhookId).Distinct().ToList();
        return ids.Count == 0 ? [] : (await q.ToListAsync(webhooks.Query().Where(w => ids.Contains(w.Id)).Select(w => new { w.Id, w.Name }), ct)).ToDictionary(w => w.Id, w => w.Name);
    }

    public static WebhookDeliveryDto ToDto(WebhookDelivery d, string webhookName, int maxAttempts) =>
        new(d.Id, d.WebhookId, webhookName, d.EventId, d.EventType, d.Status, d.Attempt, maxAttempts, d.CreatedAt, d.NextAttemptAt, d.DeliveredAt,
            d.LastStatusCode, d.LastError, d.LastDurationMs, d.ReplayOf);

    public static WebhookDeliveryDetailDto ToDetail(WebhookDelivery d, string webhookName, int maxAttempts) =>
        new(d.Id, d.WebhookId, webhookName, d.EventId, d.EventType, d.Status, d.Attempt, maxAttempts, d.CreatedAt, d.NextAttemptAt, d.DeliveredAt,
            d.LastStatusCode, d.LastError, d.LastDurationMs, d.ReplayOf, JsonSerializer.Deserialize<JsonElement>(d.Payload), d.ResponseExcerpt);
}

// ------------------------------------------------------------------------------------------------------- catalogue

public sealed record ListWebhookEventTypesQuery : IQuery<IReadOnlyList<WebhookEventTypeDto>>;

/// <summary>The event catalogue for subscription pickers. Open to anyone who manages webhooks or notification channels.</summary>
public sealed class ListWebhookEventTypesHandler(IAuthorizationService authz) : IRequestHandler<ListWebhookEventTypesQuery, IReadOnlyList<WebhookEventTypeDto>>
{
    public async Task<Result<IReadOnlyList<WebhookEventTypeDto>>> Handle(ListWebhookEventTypesQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.WebhooksManage, null, ct) && !await authz.CanAsync(Permissions.NotificationsManage, null, ct)) return Error.Forbidden();
        return EventCatalogue.All.Select(e => new WebhookEventTypeDto(e.Name, e.Category, e.Description, e.Derived)).ToList();
    }
}

// --------------------------------------------------------------------------------------------------------- read

public sealed record ListWebhooksQuery : IQuery<IReadOnlyList<WebhookDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class ListWebhooksHandler(IRepository<Webhook> webhooks, WebhookMapper mapper, IQueryExecutor q) : IRequestHandler<ListWebhooksQuery, IReadOnlyList<WebhookDto>>
{
    public async Task<Result<IReadOnlyList<WebhookDto>>> Handle(ListWebhooksQuery r, CancellationToken ct) =>
        await mapper.ToDtosAsync(await q.ToListAsync(webhooks.Query().OrderBy(w => w.Name).ThenBy(w => w.Id), ct), ct);
}

public sealed record GetWebhookQuery(Guid Id) : IQuery<WebhookDto>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class GetWebhookHandler(IRepository<Webhook> webhooks, WebhookMapper mapper, IQueryExecutor q) : IRequestHandler<GetWebhookQuery, WebhookDto>
{
    public async Task<Result<WebhookDto>> Handle(GetWebhookQuery r, CancellationToken ct)
    {
        var hook = await q.FirstOrDefaultAsync(webhooks.Query().Where(w => w.Id == r.Id), ct);
        return hook is null ? Error.NotFound("Webhook not found.") : (await mapper.ToDtosAsync([hook], ct))[0];
    }
}

// ------------------------------------------------------------------------------------------------------ create etc.

public sealed record CreatedWebhook(Guid Id, string Secret);

public sealed record CreateWebhookCommand(string Name, string Url, IReadOnlyList<string> Events, bool Active) : ICommand<CreatedWebhook>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class CreateWebhookValidator : AbstractValidator<CreateWebhookCommand>
{
    public CreateWebhookValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Url).Must(u => AddressGuard.CheckSyntax(u) is null).WithMessage(x => AddressGuard.CheckSyntax(x.Url) ?? "");
        RuleFor(x => x.Events).ValidEvents();
    }
}

public sealed class CreateWebhookHandler(
    IRepository<Webhook> webhooks, ISecretProtector secrets, IOutboundUrlGuard guard, IAuthorizationService authz, ITenantContext tenant,
    IEventCollector events, IQueryExecutor q, IClock clock) : IRequestHandler<CreateWebhookCommand, CreatedWebhook>
{
    public async Task<Result<CreatedWebhook>> Handle(CreateWebhookCommand c, CancellationToken ct)
    {
        if (await q.CountAsync(webhooks.Query(), ct) >= WebhookRules.MaxPerTenant)
            return Error.Validation(ContentMapping.Fail("name", $"At most {WebhookRules.MaxPerTenant} webhooks."));
        if (await guard.CheckAsync(c.Url, ct) is { } problem) return Error.Validation(ContentMapping.Fail("url", problem));

        var secret = WebhookSignature.GenerateSecret();
        var hook = Webhook.Create(tenant.TenantId, c.Name, c.Url, c.Events, c.Active, secrets.Protect(secret, SecretPurposes.Webhook), secret[^4..],
            (await authz.GetProfileAsync(ct)).UserId, clock.UtcNow);
        webhooks.Add(hook);
        events.Raise(Events.Changed("webhook.created", "webhook", hook.Id));
        return new CreatedWebhook(hook.Id, secret);
    }
}

public sealed record UpdateWebhookCommand(Guid Id, string Name, string Url, IReadOnlyList<string> Events, bool Active) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class UpdateWebhookValidator : AbstractValidator<UpdateWebhookCommand>
{
    public UpdateWebhookValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Url).Must(u => AddressGuard.CheckSyntax(u) is null).WithMessage(x => AddressGuard.CheckSyntax(x.Url) ?? "");
        RuleFor(x => x.Events).ValidEvents();
    }
}

public sealed class UpdateWebhookHandler(IRepository<Webhook> webhooks, IOutboundUrlGuard guard, IEventCollector events, IQueryExecutor q, IClock clock)
    : IRequestHandler<UpdateWebhookCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateWebhookCommand c, CancellationToken ct)
    {
        var hook = await q.FirstOrDefaultAsync(webhooks.Query().Where(w => w.Id == c.Id), ct);
        if (hook is null) return Error.NotFound("Webhook not found.");
        if (!string.Equals(hook.Url, c.Url.Trim(), StringComparison.Ordinal) && await guard.CheckAsync(c.Url, ct) is { } problem)
            return Error.Validation(ContentMapping.Fail("url", problem));
        hook.Update(c.Name, c.Url, c.Events, c.Active, clock.UtcNow);
        events.Raise(Events.Changed("webhook.updated", "webhook", hook.Id));
        return hook.Id;
    }
}

public sealed record DeleteWebhookCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class DeleteWebhookHandler(IRepository<Webhook> webhooks, IEventCollector events, IQueryExecutor q) : IRequestHandler<DeleteWebhookCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteWebhookCommand c, CancellationToken ct)
    {
        var hook = await q.FirstOrDefaultAsync(webhooks.Query().Where(w => w.Id == c.Id), ct);
        if (hook is null) return Error.NotFound("Webhook not found.");
        webhooks.Remove(hook);   // its delivery history goes with it
        events.Raise(Events.Changed("webhook.deleted", "webhook", hook.Id));
        return hook.Id;
    }
}

public sealed record RotateWebhookSecretCommand(Guid Id) : ICommand<CreatedWebhook>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class RotateWebhookSecretHandler(IRepository<Webhook> webhooks, ISecretProtector secrets, IEventCollector events, IQueryExecutor q, IClock clock)
    : IRequestHandler<RotateWebhookSecretCommand, CreatedWebhook>
{
    public async Task<Result<CreatedWebhook>> Handle(RotateWebhookSecretCommand c, CancellationToken ct)
    {
        var hook = await q.FirstOrDefaultAsync(webhooks.Query().Where(w => w.Id == c.Id), ct);
        if (hook is null) return Error.NotFound("Webhook not found.");
        var secret = WebhookSignature.GenerateSecret();
        hook.RotateSecret(secrets.Protect(secret, SecretPurposes.Webhook), secret[^4..], clock.UtcNow);
        events.Raise(Events.Changed("webhook.secret_rotated", "webhook", hook.Id));
        return new CreatedWebhook(hook.Id, secret);
    }
}

// --------------------------------------------------------------------------------------------------- ping / replay

/// <summary>Creates a delivery of a synthetic <c>webhook.ping</c> event. The endpoint makes the attempt straight away.</summary>
public sealed record PingWebhookCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class PingWebhookHandler(IRepository<Webhook> webhooks, IRepository<WebhookDelivery> deliveries, ITenantContext tenant, WebhookSettings settings, IQueryExecutor q, IClock clock)
    : IRequestHandler<PingWebhookCommand, Guid>
{
    public async Task<Result<Guid>> Handle(PingWebhookCommand c, CancellationToken ct)
    {
        var hook = await q.FirstOrDefaultAsync(webhooks.Query().Where(w => w.Id == c.Id), ct);
        if (hook is null) return Error.NotFound("Webhook not found.");
        if (!hook.Active) return Error.Conflict("The webhook is switched off. Turn it on to send a ping.");
        var now = clock.UtcNow;
        var id = Uuid7.NewGuid();
        var data = JsonSerializer.SerializeToElement(new { webhookId = hook.Id, message = "This is a test event from Orbit." });
        var delivery = WebhookDelivery.Create(tenant.TenantId, hook.Id, id, EventCatalogue.Ping, WebhookEnvelope.Build(id, EventCatalogue.Ping, tenant.TenantId, now, data), now, now + settings.Lease);
        deliveries.Add(delivery);
        return delivery.Id;
    }
}

/// <summary>Creates a new delivery with the same payload as an earlier one (new id, <c>replayOf</c> set). The endpoint makes the attempt straight away.</summary>
public sealed record ReplayWebhookDeliveryCommand(Guid DeliveryId) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class ReplayWebhookDeliveryHandler(
    IRepository<WebhookDelivery> deliveries, IRepository<Webhook> webhooks, WebhookSettings settings, ITenantContext tenant, IQueryExecutor q, IClock clock)
    : IRequestHandler<ReplayWebhookDeliveryCommand, Guid>
{
    public async Task<Result<Guid>> Handle(ReplayWebhookDeliveryCommand c, CancellationToken ct)
    {
        var original = await q.FirstOrDefaultAsync(deliveries.Query().Where(d => d.Id == c.DeliveryId), ct);
        if (original is null) return Error.NotFound("Delivery not found.");
        var hook = await q.FirstOrDefaultAsync(webhooks.Query().Where(w => w.Id == original.WebhookId), ct);
        if (hook is null) return Error.NotFound("The webhook no longer exists.");
        if (!hook.Active) return Error.Conflict("The webhook is switched off. Turn it on to replay a delivery.");
        var now = clock.UtcNow;
        var replay = WebhookDelivery.Create(tenant.TenantId, hook.Id, original.EventId, original.EventType, original.Payload, now, now + settings.Lease, original.Id);
        deliveries.Add(replay);
        return replay.Id;
    }
}

// ------------------------------------------------------------------------------------------------------ deliveries

/// <param name="WebhookId">Null lists across every webhook.</param>
/// <param name="EventType">An exact event name, or a prefix such as <c>workflow.*</c>.</param>
public sealed record ListWebhookDeliveriesQuery(
    Guid? WebhookId, string? Status, string? EventType, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? Limit) : IQuery<Page<WebhookDeliveryDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class ListWebhookDeliveriesValidator : AbstractValidator<ListWebhookDeliveriesQuery>
{
    public ListWebhookDeliveriesValidator()
    {
        RuleFor(x => x.Status).Must(s => s is null || DeliveryStatuses.All.Contains(s)).WithMessage("Use pending, succeeded or failed.");
        RuleFor(x => x.EventType).MaximumLength(100);
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From!.Value).When(x => x.From is not null && x.To is not null).WithMessage("'to' must not be before 'from'.");
    }
}

public sealed class ListWebhookDeliveriesHandler(IRepository<WebhookDelivery> deliveries, IRepository<Webhook> webhooks, DeliveryMapper mapper, IQueryExecutor q)
    : IRequestHandler<ListWebhookDeliveriesQuery, Page<WebhookDeliveryDto>>
{
    public async Task<Result<Page<WebhookDeliveryDto>>> Handle(ListWebhookDeliveriesQuery r, CancellationToken ct)
    {
        if (r.WebhookId is { } wid && !await q.AnyAsync(webhooks.Query().Where(w => w.Id == wid), ct)) return Error.NotFound("Webhook not found.");
        var limit = Paging.Clamp(r.Limit);
        IQueryable<WebhookDelivery> query = deliveries.Query();
        if (r.WebhookId is { } id) query = query.Where(d => d.WebhookId == id);
        if (r.Status is { } status) query = query.Where(d => d.Status == status);
        if (r.From is { } from) query = query.Where(d => d.CreatedAt >= from);
        if (r.To is { } to) query = query.Where(d => d.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(r.EventType))
        {
            var type = r.EventType.Trim();
            if (type.EndsWith(".*", StringComparison.Ordinal)) { var prefix = type[..^1]; query = query.Where(d => d.EventType.StartsWith(prefix)); }
            else query = query.Where(d => d.EventType == type);
        }
        if (Paging.ParseCursor(r.Cursor) is { } after) query = query.Where(d => d.Id.CompareTo(after) < 0);

        var rows = await q.ToListAsync(query.OrderByDescending(d => d.Id).Take(limit + 1), ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new Page<WebhookDeliveryDto>(await mapper.ToDtosAsync(rows, ct), more ? rows[^1].Id.ToString() : null);
    }
}

public sealed record GetWebhookDeliveryQuery(Guid Id) : IQuery<WebhookDeliveryDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.WebhooksManage;
}

public sealed class GetWebhookDeliveryHandler(IRepository<WebhookDelivery> deliveries, DeliveryMapper mapper, IQueryExecutor q)
    : IRequestHandler<GetWebhookDeliveryQuery, WebhookDeliveryDetailDto>
{
    public async Task<Result<WebhookDeliveryDetailDto>> Handle(GetWebhookDeliveryQuery r, CancellationToken ct)
    {
        var d = await q.FirstOrDefaultAsync(deliveries.Query().Where(x => x.Id == r.Id), ct);
        return d is null ? Error.NotFound("Delivery not found.") : await mapper.ToDetailAsync(d, ct);
    }
}

/// <summary>What a secret is encrypted for (bound into the ciphertext).</summary>
public static class SecretPurposes
{
    public const string Webhook = "webhook-secret";
    public const string ChatUrl = "chat-url";
}
