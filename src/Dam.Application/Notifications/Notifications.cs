using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Application.Webhooks;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Notifications;
using Dam.Domain.Webhooks;
using Dam.Shared.Identity;
using Dam.Shared.Notifications;
using FluentValidation;

namespace Dam.Application.Notifications;

internal static class NotificationMapping
{
    public static NotificationDto ToDto(Notification n) =>
        new(n.Id, n.Type, n.Category, n.Title, n.Body, n.LinkUrl, n.AssetId, n.ReadAt is not null, n.ReadAt, n.CreatedAt);

    public static NotificationChannelDto ToDto(NotificationChannel c) => new(c.Id, c.Kind, c.Name, c.UrlHint, c.Events, c.Active, c.CreatedAt, c.UpdatedAt);

    /// <summary>The caller's own Orbit user id. Inbox calls are about the caller only; an id never comes from the client.</summary>
    public static async Task<Result<Guid>> MeAsync(IAuthorizationService authz, CancellationToken ct)
    {
        var profile = await authz.GetProfileAsync(ct);
        return profile.UserId == Guid.Empty ? Error.Unauthorized("No Orbit user for this token.") : profile.UserId;
    }
}

// ------------------------------------------------------------------------------------------------------- inbox

/// <summary>The caller's notifications, newest first. Own rows only.</summary>
public sealed record ListNotificationsQuery(bool? Unread, string? Cursor, int? Limit) : IQuery<Page<NotificationDto>>;

public sealed class ListNotificationsHandler(IRepository<Notification> notifications, IAuthorizationService authz, IQueryExecutor q)
    : IRequestHandler<ListNotificationsQuery, Page<NotificationDto>>
{
    public async Task<Result<Page<NotificationDto>>> Handle(ListNotificationsQuery r, CancellationToken ct)
    {
        var me = await NotificationMapping.MeAsync(authz, ct);
        if (me.IsFailure) return me.Error;
        var user = me.Value;
        var limit = Paging.Clamp(r.Limit);
        IQueryable<Notification> query = notifications.Query().Where(n => n.UserId == user);
        if (r.Unread == true) query = query.Where(n => n.ReadAt == null);
        if (Paging.ParseCursor(r.Cursor) is { } after) query = query.Where(n => n.Id.CompareTo(after) < 0);
        var rows = await q.ToListAsync(query.OrderByDescending(n => n.Id).Take(limit + 1), ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new Page<NotificationDto>(rows.Select(NotificationMapping.ToDto).ToList(), more ? rows[^1].Id.ToString() : null);
    }
}

public sealed record GetUnreadCountQuery : IQuery<UnreadCountDto>;

public sealed class GetUnreadCountHandler(IRepository<Notification> notifications, IAuthorizationService authz, IQueryExecutor q)
    : IRequestHandler<GetUnreadCountQuery, UnreadCountDto>
{
    public async Task<Result<UnreadCountDto>> Handle(GetUnreadCountQuery r, CancellationToken ct)
    {
        var me = await NotificationMapping.MeAsync(authz, ct);
        if (me.IsFailure) return me.Error;
        var user = me.Value;
        return new UnreadCountDto(await q.CountAsync(notifications.Query().Where(n => n.UserId == user && n.ReadAt == null), ct));
    }
}

public sealed record MarkNotificationReadCommand(Guid Id) : ICommand<NotificationDto>;

public sealed class MarkNotificationReadHandler(IRepository<Notification> notifications, IAuthorizationService authz, IQueryExecutor q, IClock clock)
    : IRequestHandler<MarkNotificationReadCommand, NotificationDto>
{
    public async Task<Result<NotificationDto>> Handle(MarkNotificationReadCommand c, CancellationToken ct)
    {
        var me = await NotificationMapping.MeAsync(authz, ct);
        if (me.IsFailure) return me.Error;
        var user = me.Value;
        var n = await q.FirstOrDefaultAsync(notifications.Query().Where(x => x.Id == c.Id && x.UserId == user), ct);
        if (n is null) return Error.NotFound("Notification not found.");   // someone else's looks exactly like one that does not exist
        n.MarkRead(clock.UtcNow);
        return NotificationMapping.ToDto(n);
    }
}

public sealed record MarkAllNotificationsReadCommand : ICommand<MarkAllReadResultDto>;

public sealed class MarkAllNotificationsReadHandler(IRepository<Notification> notifications, IAuthorizationService authz, IQueryExecutor q, IClock clock)
    : IRequestHandler<MarkAllNotificationsReadCommand, MarkAllReadResultDto>
{
    public async Task<Result<MarkAllReadResultDto>> Handle(MarkAllNotificationsReadCommand c, CancellationToken ct)
    {
        var me = await NotificationMapping.MeAsync(authz, ct);
        if (me.IsFailure) return me.Error;
        var user = me.Value;
        var unread = await q.ToListAsync(notifications.Query().Where(n => n.UserId == user && n.ReadAt == null), ct);
        var now = clock.UtcNow;
        foreach (var n in unread) n.MarkRead(now);
        return new MarkAllReadResultDto(unread.Count);
    }
}

// -------------------------------------------------------------------------------------------------- preferences

public sealed record GetNotificationPreferencesQuery : IQuery<NotificationPreferencesDto>;

public sealed class GetNotificationPreferencesHandler(IRepository<NotificationPreference> prefs, IAuthorizationService authz, IEmailSender email, IQueryExecutor q)
    : IRequestHandler<GetNotificationPreferencesQuery, NotificationPreferencesDto>
{
    public async Task<Result<NotificationPreferencesDto>> Handle(GetNotificationPreferencesQuery r, CancellationToken ct)
    {
        var me = await NotificationMapping.MeAsync(authz, ct);
        if (me.IsFailure) return me.Error;
        return await PreferencesView.BuildAsync(prefs, q, me.Value, email.IsEnabled, ct);
    }
}

internal static class PreferencesView
{
    public static async Task<NotificationPreferencesDto> BuildAsync(IRepository<NotificationPreference> prefs, IQueryExecutor q, Guid user, bool emailAvailable, CancellationToken ct)
    {
        var stored = (await q.ToListAsync(prefs.Query().Where(p => p.UserId == user), ct)).ToDictionary(p => p.Category);
        return new NotificationPreferencesDto(NotificationCategories.All.Select(c =>
        {
            var (inApp, email) = NotificationPreference.Effective(c, stored.GetValueOrDefault(c.Key));
            return new NotificationCategoryDto(c.Key, c.Label, c.Description, inApp, email, c.DefaultInApp, c.DefaultEmail);
        }).ToList(), emailAvailable);
    }
}

/// <summary>Sets the caller's choices for the categories listed; the others keep their current setting.</summary>
public sealed record UpdateNotificationPreferencesCommand(IReadOnlyList<NotificationPreferenceInput> Preferences) : ICommand<NotificationPreferencesDto>;

public sealed class UpdateNotificationPreferencesValidator : AbstractValidator<UpdateNotificationPreferencesCommand>
{
    public UpdateNotificationPreferencesValidator()
    {
        RuleFor(x => x.Preferences).NotNull();
        RuleForEach(x => x.Preferences).Must(p => NotificationCategories.IsKnown(p.Category))
            .WithMessage($"Unknown category. Use one of: {string.Join(", ", NotificationCategories.All.Select(c => c.Key))}.");
    }
}

public sealed class UpdateNotificationPreferencesHandler(
    IRepository<NotificationPreference> prefs, IAuthorizationService authz, IEmailSender email, ITenantContext tenant, IQueryExecutor q)
    : IRequestHandler<UpdateNotificationPreferencesCommand, NotificationPreferencesDto>
{
    public async Task<Result<NotificationPreferencesDto>> Handle(UpdateNotificationPreferencesCommand c, CancellationToken ct)
    {
        var me = await NotificationMapping.MeAsync(authz, ct);
        if (me.IsFailure) return me.Error;
        var user = me.Value;
        var existing = (await q.ToListAsync(prefs.Query().Where(p => p.UserId == user), ct)).ToDictionary(p => p.Category);
        foreach (var input in c.Preferences)
        {
            if (existing.TryGetValue(input.Category, out var row)) row.Set(input.InApp, input.Email);
            else
            {
                var created = NotificationPreference.Create(tenant.TenantId, user, input.Category, input.InApp, input.Email);
                prefs.Add(created);
                existing[input.Category] = created;
            }
        }
        // Reflect what was just set without another round trip.
        return new NotificationPreferencesDto(NotificationCategories.All.Select(cat =>
        {
            var (inApp, mail) = NotificationPreference.Effective(cat, existing.GetValueOrDefault(cat.Key));
            return new NotificationCategoryDto(cat.Key, cat.Label, cat.Description, inApp, mail, cat.DefaultInApp, cat.DefaultEmail);
        }).ToList(), email.IsEnabled);
    }
}

// ---------------------------------------------------------------------------------------------------- channels

internal static class ChannelRules
{
    public const int MaxPerTenant = 50;
}

public sealed record ListNotificationChannelsQuery : IQuery<IReadOnlyList<NotificationChannelDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.NotificationsManage;
}

public sealed class ListNotificationChannelsHandler(IRepository<NotificationChannel> channels, IQueryExecutor q)
    : IRequestHandler<ListNotificationChannelsQuery, IReadOnlyList<NotificationChannelDto>>
{
    public async Task<Result<IReadOnlyList<NotificationChannelDto>>> Handle(ListNotificationChannelsQuery r, CancellationToken ct) =>
        (await q.ToListAsync(channels.Query().OrderBy(c => c.Name).ThenBy(c => c.Id), ct)).Select(NotificationMapping.ToDto).ToList();
}

public sealed record GetNotificationChannelQuery(Guid Id) : IQuery<NotificationChannelDto>, IAuthorizedRequest
{
    public string Permission => Permissions.NotificationsManage;
}

public sealed class GetNotificationChannelHandler(IRepository<NotificationChannel> channels, IQueryExecutor q) : IRequestHandler<GetNotificationChannelQuery, NotificationChannelDto>
{
    public async Task<Result<NotificationChannelDto>> Handle(GetNotificationChannelQuery r, CancellationToken ct)
    {
        var c = await q.FirstOrDefaultAsync(channels.Query().Where(x => x.Id == r.Id), ct);
        return c is null ? Error.NotFound("Channel not found.") : NotificationMapping.ToDto(c);
    }
}

public sealed record CreateNotificationChannelCommand(string Kind, string Name, string? Url, IReadOnlyList<string> Events, bool Active) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.NotificationsManage;
}

public sealed class CreateNotificationChannelValidator : AbstractValidator<CreateNotificationChannelCommand>
{
    public CreateNotificationChannelValidator()
    {
        RuleFor(x => x.Kind).Must(k => ChannelKinds.All.Contains(k)).WithMessage("Use 'slack' or 'teams'.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Url).Must(u => AddressGuard.CheckSyntax(u) is null).WithMessage(x => AddressGuard.CheckSyntax(x.Url) ?? "");
        RuleFor(x => x.Events).ValidEvents();
    }
}

public sealed class CreateNotificationChannelHandler(
    IRepository<NotificationChannel> channels, ISecretProtector secrets, IOutboundUrlGuard guard, IAuthorizationService authz, ITenantContext tenant,
    IEventCollector events, IQueryExecutor q, IClock clock) : IRequestHandler<CreateNotificationChannelCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateNotificationChannelCommand c, CancellationToken ct)
    {
        if (await q.CountAsync(channels.Query(), ct) >= ChannelRules.MaxPerTenant)
            return Error.Validation(ContentMapping.Fail("name", $"At most {ChannelRules.MaxPerTenant} channels."));
        var url = c.Url!.Trim();
        if (await guard.CheckAsync(url, ct) is { } problem) return Error.Validation(ContentMapping.Fail("url", problem));
        var channel = NotificationChannel.Create(tenant.TenantId, c.Kind, c.Name, secrets.Protect(url, SecretPurposes.ChatUrl), NotificationChannel.Hint(url),
            c.Events, c.Active, (await authz.GetProfileAsync(ct)).UserId, clock.UtcNow);
        channels.Add(channel);
        events.Raise(Events.Changed("notification_channel.created", "notification_channel", channel.Id));
        return channel.Id;
    }
}

public sealed record UpdateNotificationChannelCommand(Guid Id, string Kind, string Name, string? Url, IReadOnlyList<string> Events, bool Active) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.NotificationsManage;
}

public sealed class UpdateNotificationChannelValidator : AbstractValidator<UpdateNotificationChannelCommand>
{
    public UpdateNotificationChannelValidator()
    {
        RuleFor(x => x.Kind).Must(k => ChannelKinds.All.Contains(k)).WithMessage("Use 'slack' or 'teams'.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Url).Must(u => AddressGuard.CheckSyntax(u) is null).When(x => !string.IsNullOrWhiteSpace(x.Url)).WithMessage(x => AddressGuard.CheckSyntax(x.Url) ?? "");
        RuleFor(x => x.Events).ValidEvents();
    }
}

public sealed class UpdateNotificationChannelHandler(
    IRepository<NotificationChannel> channels, ISecretProtector secrets, IOutboundUrlGuard guard, IEventCollector events, IQueryExecutor q, IClock clock)
    : IRequestHandler<UpdateNotificationChannelCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateNotificationChannelCommand c, CancellationToken ct)
    {
        var channel = await q.FirstOrDefaultAsync(channels.Query().Where(x => x.Id == c.Id), ct);
        if (channel is null) return Error.NotFound("Channel not found.");
        var now = clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(c.Url))
        {
            var url = c.Url.Trim();
            if (await guard.CheckAsync(url, ct) is { } problem) return Error.Validation(ContentMapping.Fail("url", problem));
            channel.ReplaceUrl(secrets.Protect(url, SecretPurposes.ChatUrl), NotificationChannel.Hint(url), now);
        }
        channel.Update(c.Kind, c.Name, c.Events, c.Active, now);
        events.Raise(Events.Changed("notification_channel.updated", "notification_channel", channel.Id));
        return channel.Id;
    }
}

public sealed record DeleteNotificationChannelCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.NotificationsManage;
}

public sealed class DeleteNotificationChannelHandler(IRepository<NotificationChannel> channels, IEventCollector events, IQueryExecutor q)
    : IRequestHandler<DeleteNotificationChannelCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteNotificationChannelCommand c, CancellationToken ct)
    {
        var channel = await q.FirstOrDefaultAsync(channels.Query().Where(x => x.Id == c.Id), ct);
        if (channel is null) return Error.NotFound("Channel not found.");
        channels.Remove(channel);
        events.Raise(Events.Changed("notification_channel.deleted", "notification_channel", channel.Id));
        return channel.Id;
    }
}

/// <summary>Posts a test message to the channel and reports what the chat service answered. Writes nothing.</summary>
public sealed record TestNotificationChannelQuery(Guid Id) : IQuery<ChannelTestResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.NotificationsManage;
}

public sealed class TestNotificationChannelHandler(
    IRepository<NotificationChannel> channels, ISecretProtector secrets, IChatSender chat, IQueryExecutor q, NotificationSettings settings)
    : IRequestHandler<TestNotificationChannelQuery, ChannelTestResultDto>
{
    public async Task<Result<ChannelTestResultDto>> Handle(TestNotificationChannelQuery r, CancellationToken ct)
    {
        var channel = await q.FirstOrDefaultAsync(channels.Query().Where(x => x.Id == r.Id), ct);
        if (channel is null) return Error.NotFound("Channel not found.");
        var url = secrets.Unprotect(channel.UrlCipher, SecretPurposes.ChatUrl);
        var result = await chat.SendAsync(channel.Kind, url,
            new ChatMessage("Orbit test message", $"This is a test message for the channel '{channel.Name}'. If you can read it, notifications will arrive here.",
                EmailTemplates.Absolute(settings.ConsolePublicUrl, "/")), ct);
        return new ChannelTestResultDto(result.Ok, result.StatusCode, result.Error, result.DurationMs);
    }
}
