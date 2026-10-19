using Dam.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace Dam.Infrastructure.Notifications;

/// <summary>SMTP settings (env: DAM_SMTP_HOST, _PORT, _USER, _PASSWORD, _FROM, _TLS).</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "Orbit <orbit@localhost>";
    /// <summary>auto (default: negotiate), starttls, tls (implicit TLS, usually port 465) or none (plain text, for a local test server).</summary>
    public string Tls { get; set; } = "auto";

    public static SmtpOptions? Read(IConfiguration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg["DAM_SMTP_HOST"])) return null;
        return new SmtpOptions
        {
            Host = cfg["DAM_SMTP_HOST"]!.Trim(),
            Port = int.TryParse(cfg["DAM_SMTP_PORT"], out var p) ? p : 587,
            User = cfg["DAM_SMTP_USER"],
            Password = cfg["DAM_SMTP_PASSWORD"],
            From = cfg["DAM_SMTP_FROM"] is { Length: > 0 } f ? f : "Orbit <orbit@localhost>",
            Tls = (cfg["DAM_SMTP_TLS"] ?? "auto").Trim().ToLowerInvariant(),
        };
    }

    public SecureSocketOptions Socket => Tls switch
    {
        "none" => SecureSocketOptions.None,
        "starttls" => SecureSocketOptions.StartTls,
        "tls" => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}

/// <summary>Sends mail through MailKit: one short-lived connection per message, HTML and plain-text parts together.</summary>
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public bool IsEnabled => true;

    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(new MailboxAddress(m.ToName ?? "", m.ToAddress));
        message.Subject = m.Subject;
        message.Body = new BodyBuilder { HtmlBody = m.Html, TextBody = m.Text }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(options.Host, options.Port, options.Socket, ct);
        if (!string.IsNullOrEmpty(options.User)) await client.AuthenticateAsync(options.User, options.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Used when no SMTP server is configured: email is simply not available.</summary>
public sealed class NoOpEmailSender : IEmailSender
{
    public bool IsEnabled => false;
    public Task SendAsync(EmailMessage message, CancellationToken ct) => Task.CompletedTask;
}
