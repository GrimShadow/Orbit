using System.Globalization;
using System.Net;
using System.Text;
using Dam.Domain.Notifications;

namespace Dam.Application.Notifications;

/// <summary>What a notification says, before it is addressed to anyone.</summary>
/// <param name="Type">The event name it stems from.</param>
/// <param name="LinkPath">A path inside the console.</param>
public sealed record NotificationContent(string Type, string Category, string Title, string Body, string LinkPath, Guid? AssetId);

/// <summary>
/// The rules catalogue (ADR-0031): which events become notifications, in which category, with what words. Pure functions, no customer
/// content; who receives them is decided separately by <see cref="NotificationPlanner"/>. English only for now (the console shows the
/// text as written).
/// </summary>
public static class NotificationRules
{
    public const string TasksPath = "/tasks";
    public static string AssetPath(Guid assetId) => $"/library/assets/{assetId}";

    private static string When(DateTimeOffset t) => t.ToUniversalTime().ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Quote(string title) => string.IsNullOrWhiteSpace(title) ? "an asset" : $"'{title}'";

    public static NotificationContent TaskCreated(Guid assetId, string assetTitle, string stepName, string workflowName, DateTimeOffset? dueAt) =>
        new("workflow.task.created", NotificationCategories.WorkflowTask,
            $"Approval needed: {Short(assetTitle)}",
            $"{Quote(assetTitle)} is waiting for your decision at the '{stepName}' step of '{workflowName}'." + (dueAt is { } d ? $" It is due {When(d)}." : ""),
            TasksPath, assetId);

    /// <param name="decision">approve or reject.</param>
    /// <param name="instanceStatus">running, approved or rejected: whether this decision ended the workflow.</param>
    public static NotificationContent TaskCompleted(Guid assetId, string assetTitle, string stepName, string decision, string instanceStatus, string? comment, string? deciderName)
    {
        var by = string.IsNullOrWhiteSpace(deciderName) ? "" : $" by {deciderName}";
        if (decision == "reject")
            return new("workflow.task.completed", NotificationCategories.WorkflowOutcome,
                $"Rejected: {Short(assetTitle)}",
                $"{Quote(assetTitle)} was rejected{by} at the '{stepName}' step" + (string.IsNullOrWhiteSpace(comment) ? "." : $": {comment}"),
                AssetPath(assetId), assetId);
        if (instanceStatus == "approved")
            return new("workflow.task.completed", NotificationCategories.WorkflowOutcome,
                $"Approved: {Short(assetTitle)}",
                $"{Quote(assetTitle)} was approved{by} at the '{stepName}' step and the workflow is complete." + (string.IsNullOrWhiteSpace(comment) ? "" : $" Comment: {comment}"),
                AssetPath(assetId), assetId);
        return new("workflow.task.completed", NotificationCategories.WorkflowOutcome,
            $"Step approved: {Short(assetTitle)}",
            $"{Quote(assetTitle)} was approved{by} at the '{stepName}' step. The next step is now open." + (string.IsNullOrWhiteSpace(comment) ? "" : $" Comment: {comment}"),
            AssetPath(assetId), assetId);
    }

    public static NotificationContent SlaBreached(Guid assetId, string assetTitle, string stepName, DateTimeOffset dueAt) =>
        new("workflow.sla.breached", NotificationCategories.Sla,
            $"Overdue: {Short(assetTitle)}",
            $"The '{stepName}' step for {Quote(assetTitle)} was due {When(dueAt)} and has not been decided. It has been escalated.",
            TasksPath, assetId);

    /// <summary>Only a move to expired or archived is worth telling the owner about; anything else returns null.</summary>
    public static NotificationContent? AssetLifecycle(Guid assetId, string assetTitle, string to, string? reason) => to switch
    {
        "expired" => new("asset.status.changed", NotificationCategories.AssetLifecycle,
            $"Expired: {Short(assetTitle)}",
            $"{Quote(assetTitle)} has expired and is no longer available to channels." + Reason(reason), AssetPath(assetId), assetId),
        "archived" => new("asset.status.changed", NotificationCategories.AssetLifecycle,
            $"Archived: {Short(assetTitle)}",
            $"{Quote(assetTitle)} has been archived." + Reason(reason), AssetPath(assetId), assetId),
        _ => null,
    };

    /// <summary>Told to an asset's owner 30, 7 and 1 days before its licence ends (ADR-0033).</summary>
    public static NotificationContent LicenceExpiring(Guid assetId, string assetTitle, string rightsName, DateOnly validTo, int daysRemaining) =>
        new("rights.expiring", NotificationCategories.Rights,
            $"Licence for {Subject(assetTitle)} expires {Dam.Domain.Licensing.RightsRules.DaysPhrase(daysRemaining)}",
            $"The licence '{rightsName}' ends on {validTo.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}. Renew it or assign other rights to keep {Quote(assetTitle)} available.",
            AssetPath(assetId), assetId);

    public static NotificationContent LicenceExpired(Guid assetId, string assetTitle, string rightsName, DateOnly validTo) =>
        new("rights.expired", NotificationCategories.Rights,
            $"Licence for {Subject(assetTitle)} has expired",
            $"The licence '{rightsName}' ended on {validTo.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}. {Quote(assetTitle)} can no longer be published or delivered under it; a published asset is moved to expired.",
            AssetPath(assetId), assetId);

    /// <summary>The console deep link to a comment: the asset page, which opens the thread.</summary>
    public static string CommentPath(Guid assetId, Guid commentId) => $"{AssetPath(assetId)}?comment={commentId}";

    private static string Subject(string title) => string.IsNullOrWhiteSpace(title) ? "an asset" : $"'{Short(title)}'";
    private static string Who(string? name) => string.IsNullOrWhiteSpace(name) ? "Someone" : name.Trim();

    /// <param name="eventType">comment.added, or comment.edited when an edit added the mention.</param>
    /// <param name="excerpt">A short plain-text excerpt (mention tokens already shown as @Name).</param>
    public static NotificationContent Mention(string eventType, Guid assetId, string assetTitle, Guid commentId, string? authorName, string excerpt) =>
        new(eventType, NotificationCategories.Mention, $"{Who(authorName)} mentioned you on {Subject(assetTitle)}", excerpt, CommentPath(assetId, commentId), assetId);

    public static NotificationContent Reply(Guid assetId, string assetTitle, Guid commentId, string? authorName, string excerpt) =>
        new("comment.added", NotificationCategories.Comments, $"{Who(authorName)} replied to your comment on {Subject(assetTitle)}", excerpt, CommentPath(assetId, commentId), assetId);

    public static NotificationContent NewComment(Guid assetId, string assetTitle, Guid commentId, string? authorName, string excerpt, bool annotation) =>
        new("comment.added", NotificationCategories.Comments,
            $"{Who(authorName)} {(annotation ? "added an annotation to" : "commented on")} {Subject(assetTitle)}", excerpt, CommentPath(assetId, commentId), assetId);

    public static NotificationContent ThreadResolved(Guid assetId, string assetTitle, Guid commentId, string? resolverName, string excerpt) =>
        new("comment.resolved", NotificationCategories.Comments, $"{Who(resolverName)} resolved your comment on {Subject(assetTitle)}", excerpt, CommentPath(assetId, commentId), assetId);

    /// <summary>A one-line description of any catalogue event, for chat channels (which hear about everything they subscribe to).</summary>
    public static (string Title, string Body) Describe(string eventName, string? assetTitle, string? detail)
    {
        var what = eventName switch
        {
            "asset.created" => "Asset created",
            "asset.metadata.updated" => "Asset updated",
            "asset.status.changed" => "Asset status changed",
            "asset.published" => "Asset published",
            "asset.expired" => "Asset expired",
            "asset.archived" => "Asset archived",
            "asset.deleted" => "Asset deleted",
            "asset.version.added" => "New version added",
            "asset.legal_hold.changed" => "Legal hold changed",
            "rendition.ready" => "Rendition ready",
            "collection.updated" => "Collection updated",
            "collection.assets_changed" => "Collection contents changed",
            "workflow.task.created" => "Approval needed",
            "workflow.task.completed" => "Approval decided",
            "workflow.sla.breached" => "Approval overdue",
            "comment.added" => "New comment",
            "comment.resolved" => "Comment thread resolved",
            "comment.reopened" => "Comment thread reopened",
            "rights.expiring" => "Licence expiring",
            "rights.expired" => "Licence expired",
            _ => eventName,
        };
        var subject = string.IsNullOrWhiteSpace(assetTitle) ? "" : Short(assetTitle);
        return (subject.Length == 0 ? what : $"{what}: {subject}", detail ?? "");
    }

    private static string Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason}";

    private static string Short(string title)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "an asset" : title.Trim();
        return t.Length <= 80 ? t : t[..79] + "…";
    }
}

/// <summary>Who gets which channel, once the recipients and their preferences are known.</summary>
public static class RecipientPlanner
{
    public sealed record Delivery(Guid UserId, bool InApp, bool Email);

    /// <summary>
    /// Applies each recipient's preference for the category (their stored choice, else the category default). A person with neither channel
    /// on is left out. Email is only planned when the server can send it. Each person appears at most once, in input order.
    /// </summary>
    public static IReadOnlyList<Delivery> Plan(NotificationCategory category, IEnumerable<Guid> recipients, IReadOnlyDictionary<Guid, NotificationPreference> stored, bool emailAvailable)
    {
        var result = new List<Delivery>();
        foreach (var user in recipients.Distinct())
        {
            var (inApp, email) = NotificationPreference.Effective(category, stored.GetValueOrDefault(user));
            email &= emailAvailable;
            if (inApp || email) result.Add(new Delivery(user, inApp, email));
        }
        return result;
    }
}

public sealed record EmailModel(string TenantName, string Title, string Body, string? LinkUrl, string LinkLabel = "Open in Orbit");

public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// The one email layout every notification uses: tenant name in the header, the title, the message, one button, a footer. HTML and
/// plain-text parts are rendered together from the same model; everything that came from users or data is escaped.
/// </summary>
public static class EmailTemplates
{
    public static RenderedEmail Render(EmailModel m)
    {
        var tenant = Clean(m.TenantName);
        var title = Clean(m.Title);
        var link = IsHttp(m.LinkUrl) ? m.LinkUrl : null;

        var subject = tenant.Length == 0 ? title : $"[{tenant}] {title}";
        if (subject.Length > 200) subject = subject[..199] + "…";

        var text = new StringBuilder();
        if (tenant.Length > 0) text.Append(tenant).Append("\n\n");
        text.Append(title).Append("\n\n").Append(m.Body.Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).Append("\n\n");
        if (link is not null) text.Append(m.LinkLabel).Append(": ").Append(link).Append("\n\n");
        text.Append("-- \nYou receive this because of your notification settings in Orbit.\n");

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(H(title)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f4f4f2;font-family:-apple-system,'Segoe UI',Helvetica,Arial,sans-serif;color:#1c2321;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"background:#f4f4f2;padding:24px 0;\"><tr><td align=\"center\">")
            .Append("<table role=\"presentation\" width=\"560\" cellspacing=\"0\" cellpadding=\"0\" style=\"max-width:560px;width:100%;background:#ffffff;border-radius:8px;overflow:hidden;\">");
        if (tenant.Length > 0)
            html.Append("<tr><td style=\"background:#2f4a3e;color:#ffffff;padding:16px 24px;font-size:14px;font-weight:600;\">").Append(H(tenant)).Append("</td></tr>");
        html.Append("<tr><td style=\"padding:24px;\"><h1 style=\"margin:0 0 12px 0;font-size:20px;line-height:1.3;\">").Append(H(title)).Append("</h1>");
        foreach (var paragraph in m.Body.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            html.Append("<p style=\"margin:0 0 12px 0;font-size:15px;line-height:1.5;\">").Append(H(paragraph).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
        if (link is not null)
            html.Append("<p style=\"margin:20px 0 4px 0;\"><a href=\"").Append(H(link)).Append("\" style=\"display:inline-block;background:#2f4a3e;color:#ffffff;text-decoration:none;")
                .Append("padding:10px 18px;border-radius:6px;font-size:14px;font-weight:600;\">").Append(H(m.LinkLabel)).Append("</a></p>");
        html.Append("</td></tr><tr><td style=\"padding:12px 24px 20px 24px;font-size:12px;color:#6b7370;\">You receive this because of your notification settings in Orbit.</td></tr>")
            .Append("</table></td></tr></table></body></html>");

        return new RenderedEmail(subject, html.ToString(), text.ToString());
    }

    /// <summary>Joins the console's public address and a console path; null when no public address is configured.</summary>
    public static string? Absolute(string? consolePublicUrl, string path) =>
        string.IsNullOrWhiteSpace(consolePublicUrl) || !IsHttp(consolePublicUrl) ? null : consolePublicUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static bool IsHttp(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    /// <summary>Header values must stay on one line.</summary>
    private static string Clean(string s) => string.Join(' ', s.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string H(string s) => WebUtility.HtmlEncode(s);
}
