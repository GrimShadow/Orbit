using System.Diagnostics;
using System.Net.Http.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Notifications;

namespace Dam.Infrastructure.Notifications;

/// <summary>
/// Posts a short message to a Slack incoming webhook (<c>{ "text": ... }</c> in Slack's mrkdwn) or a Microsoft Teams workflow webhook
/// (a message carrying one Adaptive Card). Uses the same guarded HTTP client as webhooks.
/// </summary>
public sealed class ChatSender(OutboundHttp http, WebhookSettings settings) : IChatSender
{
    public async Task<ChatPostResult> SendAsync(string kind, string url, ChatMessage message, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(Payload(kind, message)) };
            request.Headers.UserAgent.ParseAdd("Orbit-Notifications/1.0");
            using var response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new ChatPostResult(true, code, null, (int)sw.ElapsedMilliseconds)
                : new ChatPostResult(false, code, $"The chat service answered {code}.", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ChatPostResult(false, null, OutboundHttp.Explain(ex, timeout.IsCancellationRequested), (int)sw.ElapsedMilliseconds);
        }
    }

    public static object Payload(string kind, ChatMessage m) => kind == ChannelKinds.Teams ? Teams(m) : Slack(m);

    private static object Slack(ChatMessage m)
    {
        var text = $"*{Escape(m.Title)}*" + (string.IsNullOrWhiteSpace(m.Body) ? "" : "\n" + Escape(m.Body));
        if (m.LinkUrl is { Length: > 0 } link) text += $"\n<{link}|Open in Orbit>";
        return new { text };
    }

    /// <summary>Slack treats &amp;, &lt; and &gt; specially in message text.</summary>
    private static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static object Teams(ChatMessage m)
    {
        var body = new List<object> { new { type = "TextBlock", text = m.Title, weight = "Bolder", size = "Medium", wrap = true } };
        if (!string.IsNullOrWhiteSpace(m.Body)) body.Add(new { type = "TextBlock", text = m.Body, wrap = true });
        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (m.LinkUrl is { Length: > 0 } link) card["actions"] = new object[] { new { type = "Action.OpenUrl", title = "Open in Orbit", url = link } };
        return new { type = "message", attachments = new object[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null, content = card } } };
    }
}
