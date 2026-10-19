namespace Dam.Shared.Notifications;

/// <param name="Type">The event that caused it, e.g. <c>workflow.task.created</c>.</param>
/// <param name="Category">workflow_task, workflow_outcome, sla or asset_lifecycle.</param>
/// <param name="LinkUrl">A path inside the console (for example <c>/tasks</c> or <c>/library/assets/{id}</c>), never an absolute URL.</param>
public sealed record NotificationDto(
    Guid Id, string Type, string Category, string Title, string Body, string? LinkUrl, Guid? AssetId, bool Read, DateTimeOffset? ReadAt,
    DateTimeOffset CreatedAt);

public sealed record UnreadCountDto(int Count);

public sealed record MarkAllReadResultDto(int Marked);

/// <param name="Key">The category id used by <c>PUT /notifications/preferences</c>.</param>
/// <param name="InApp">The caller's current setting (their own choice, else the default).</param>
/// <param name="Email">The caller's current setting (their own choice, else the default).</param>
public sealed record NotificationCategoryDto(string Key, string Label, string Description, bool InApp, bool Email, bool DefaultInApp, bool DefaultEmail);

/// <param name="EmailAvailable">False when the server has no SMTP configured, so email settings have no effect yet.</param>
public sealed record NotificationPreferencesDto(IReadOnlyList<NotificationCategoryDto> Categories, bool EmailAvailable);

public sealed record NotificationPreferenceInput(string Category, bool InApp, bool Email);

/// <param name="Preferences">Only the categories listed are changed.</param>
public sealed record UpdateNotificationPreferencesInput(IReadOnlyList<NotificationPreferenceInput> Preferences);

/// <param name="Kind">slack or teams.</param>
/// <param name="UrlHint">The channel's webhook URL is a secret and is never returned; this is a short masked form such as <c>https://hooks.slack.com/…a1b2</c>.</param>
/// <param name="Events">Event names or patterns, as for webhooks (<c>workflow.*</c>, <c>*</c>).</param>
public sealed record NotificationChannelDto(
    Guid Id, string Kind, string Name, string UrlHint, IReadOnlyList<string> Events, bool Active, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Url">Required when creating; on update, null keeps the stored URL.</param>
public sealed record NotificationChannelInput(string Kind, string Name, string? Url, IReadOnlyList<string> Events, bool Active = true);

public sealed record ChannelTestResultDto(bool Ok, int? StatusCode, string? Error, int DurationMs);
