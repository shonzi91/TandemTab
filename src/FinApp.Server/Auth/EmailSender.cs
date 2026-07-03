using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace FinApp.Server.Auth;

/// <summary>Sends transactional emails (currently just address verification).</summary>
public interface IEmailSender
{
    /// <summary>Whether a real transport is configured. When false, links are logged instead of sent.</summary>
    bool IsConfigured { get; }
    Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}

/// <summary>Email settings, bound from the "Email" config section.</summary>
public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; } = true;
    public string FromAddress { get; set; } = "no-reply@tandemtab.app";
    public string FromName { get; set; } = "TandemTab";
    /// <summary>Public base URL the app is reached at, used to build links in emails (e.g. https://tandemtab.app).</summary>
    public string? AppBaseUrl { get; set; }
}

/// <summary>
/// SMTP-backed sender when <c>Email:Host</c> is configured; otherwise a safe no-op that logs the message
/// (so the verification flow works end-to-end in dev, and the app doesn't fall over before SMTP creds exist).
/// Uses the built-in <see cref="SmtpClient"/> — no extra package, keeping the dependency-scan surface small.
/// </summary>
public sealed class EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Host);

    public async Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            // No transport yet: log the text body (which carries the link) so nothing silently vanishes.
            logger.LogWarning("Email not configured; would send to {To} — {Subject}\n{Body}", toEmail, subject, textBody);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(toEmail);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(_options.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_options.Username, _options.Password),
        };
        await client.SendMailAsync(message, ct);
    }
}
