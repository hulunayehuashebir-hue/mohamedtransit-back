using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MohamedTransit.Application.Helper;

namespace MohamedTransit.Application.Services;

public class EmailSenderService
{
    private readonly EmailSettings? _emailSettings;
    private readonly ILogger<EmailSenderService> _logger;

    public EmailSenderService(
        IOptions<Settings> emailSettings,
        ILogger<EmailSenderService> logger)
    {
        ArgumentNullException.ThrowIfNull(emailSettings);
        ArgumentNullException.ThrowIfNull(logger);

        _emailSettings = emailSettings.Value?.EmailSettings;
        _logger = logger;
    }

    public Task SendEmailAsync(
        string message,
        string subject,
        string[] toAddress,
        string[]? ccAddress = null,
        string[]? attachements = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(
            message,
            subject,
            toAddress,
            ccAddress,
            attachements,
            cancellationToken);
    }

    public async Task Execute(
        string message,
        string subject,
        string[]? toAddress,
        string[]? ccAddress,
        string[]? attachements,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        try
        {
            var settings = _emailSettings
                ?? throw new InvalidOperationException("SMTP email settings are not configured.");
            if (string.IsNullOrWhiteSpace(settings.MailServer) ||
                settings.MailPort is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(settings.Sender) ||
                string.IsNullOrWhiteSpace(settings.Password))
            {
                throw new InvalidOperationException("SMTP email settings are incomplete.");
            }

            var senderName = string.IsNullOrWhiteSpace(settings.SenderName)
                ? "Mohamed Transit Group"
                : settings.SenderName;
            var email = new MimeMessage();
            email.From.Add(new MailboxAddress(senderName, settings.Sender.Trim()));
            AddRecipients(toAddress, email.To);
            AddRecipients(ccAddress, email.Cc);

            if (email.To.Count == 0 && email.Cc.Count == 0)
                throw new ArgumentException("At least one valid To or Cc recipient is required.", nameof(toAddress));

            email.Subject = subject;
            var bodyBuilder = new BodyBuilder { HtmlBody = message };

            if (attachements is not null)
            {
                foreach (var attachment in attachements)
                {
                    if (!string.IsNullOrWhiteSpace(attachment))
                        bodyBuilder.Attachments.Add(attachment);
                }
            }

            email.Body = bodyBuilder.ToMessageBody();
            var secureSocketOptions = settings.MailPort switch
            {
                465 => SecureSocketOptions.SslOnConnect,
                587 => SecureSocketOptions.StartTls,
                _ => SecureSocketOptions.Auto
            };

            using var smtp = new SmtpClient
            {
                Timeout = 25000,
                CheckCertificateRevocation = false
            };

            await smtp.ConnectAsync(
                settings.MailServer,
                settings.MailPort,
                secureSocketOptions,
                cancellationToken);

            await smtp.AuthenticateAsync(
                settings.Sender.Trim(),
                settings.Password,
                cancellationToken);

            await smtp.SendAsync(email, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to send email with subject {Subject} to {RecipientCount} recipient(s). Error: {Message}",
                subject,
                (toAddress?.Count(address => !string.IsNullOrWhiteSpace(address)) ?? 0) +
                (ccAddress?.Count(address => !string.IsNullOrWhiteSpace(address)) ?? 0),
                exception.Message);
            throw new InvalidOperationException("Email could not be sent. Please try again later.");
        }
    }

    private static void AddRecipients(IEnumerable<string>? addresses, InternetAddressList recipients)
    {
        if (addresses is null)
        {
            return;
        }

        foreach (var address in addresses)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                recipients.Add(MailboxAddress.Parse(address.Trim()));
            }
        }
    }
}