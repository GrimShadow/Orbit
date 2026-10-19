using Dam.Application.Messaging;
using Dam.Application.Notifications;
using Dam.Shared.Identity;
using Dam.Shared.Notifications;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>The caller's inbox and preferences (authentication only; always the caller's own rows) and the tenant's chat channels (notifications.manage). ADR-0031.</summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this RouteGroupBuilder v1)
    {
        var inbox = v1.MapGroup("/notifications").WithTags("Notifications");

        inbox.MapGet("", (IDispatcher d, CancellationToken ct, bool? unread, string? cursor, int? limit) =>
                Run(d, new ListNotificationsQuery(unread, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListNotifications").WithSummary("The caller's own notifications, newest first (unread=true for unread only; limit up to 200; cursor from nextCursor)")
            .Produces<Page<NotificationDto>>();
        inbox.MapGet("/unread-count", (IDispatcher d, CancellationToken ct) => Run(d, new GetUnreadCountQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetUnreadNotificationCount").WithSummary("How many unread notifications the caller has (for the badge)").Produces<UnreadCountDto>();
        inbox.MapPost("/read-all", (IDispatcher d, CancellationToken ct) => Run(d, new MarkAllNotificationsReadCommand(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("MarkAllNotificationsRead").WithSummary("Marks all of the caller's notifications read").Produces<MarkAllReadResultDto>();
        inbox.MapPost("/{id:guid}/read", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new MarkNotificationReadCommand(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("MarkNotificationRead").WithSummary("Marks one of the caller's notifications read (404 for anyone else's)").Produces<NotificationDto>();
        inbox.MapGet("/preferences", (IDispatcher d, CancellationToken ct) => Run(d, new GetNotificationPreferencesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetNotificationPreferences")
            .WithSummary("The notification categories with the caller's in-app and email settings (their own choice, else the default), and whether the server can send email")
            .Produces<NotificationPreferencesDto>();
        inbox.MapPut("/preferences", (UpdateNotificationPreferencesInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateNotificationPreferencesCommand(r.Preferences ?? []), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateNotificationPreferences").WithSummary("Sets the caller's settings for the categories listed; others keep theirs").Produces<NotificationPreferencesDto>();

        var channels = v1.MapGroup("/notification-channels").WithTags("Notifications");

        channels.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListNotificationChannelsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListNotificationChannels").WithSummary("Slack and Teams channels that receive messages for matching events. Needs notifications.manage")
            .Produces<IReadOnlyList<NotificationChannelDto>>();
        channels.MapPost("", (NotificationChannelInput r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateNotificationChannelCommand(r.Kind, r.Name, r.Url, r.Events ?? [], r.Active), id => new GetNotificationChannelQuery(id), ct, createdAt: "/api/v1/notification-channels"))
            .WithName("CreateNotificationChannel").WithSummary("Adds a chat channel. The incoming-webhook URL is stored encrypted and never returned").Produces<NotificationChannelDto>(StatusCodes.Status201Created);
        channels.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetNotificationChannelQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetNotificationChannel").Produces<NotificationChannelDto>();
        channels.MapPut("/{id:guid}", (Guid id, NotificationChannelInput r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateNotificationChannelCommand(id, r.Kind, r.Name, r.Url, r.Events ?? [], r.Active), cid => new GetNotificationChannelQuery(cid), ct))
            .WithName("UpdateNotificationChannel").WithSummary("Replaces a channel's settings; a null url keeps the stored one").Produces<NotificationChannelDto>();
        channels.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteNotificationChannelCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteNotificationChannel");
        channels.MapPost("/{id:guid}/test", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new TestNotificationChannelQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("TestNotificationChannel").WithSummary("Posts a test message to the channel and reports what the chat service answered").Produces<ChannelTestResultDto>();
    }
}
