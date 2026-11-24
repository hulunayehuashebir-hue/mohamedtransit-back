using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MohamedTransit.Application.Helper;

namespace MohamedTransit.Application.Services;

public class EmailSenderService
{
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<EmailSenderService> _logger;

    public EmailSenderService(
        IOptions<Settings> emailSettings,
        ILogger<EmailSenderService> logger)
    {
        ArgumentNullException.ThrowIfNull(emailSettings);
        ArgumentNullException.ThrowIfNull(logger);

        _emailSettings = emailSettings.Value?.EmailSettings
            ?? throw new InvalidOperationException("SMTP email settings are not configured.");
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_emailSettings.MailServer))
        {
            throw new InvalidOperationException("EmailSettings:MailServer must be configured.");
        }

        if (_emailSettings.MailPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("EmailSettings:MailPort must be a valid TCP port.");
        }

        if (string.IsNullOrWhiteSpace(_emailSettings.Sender))
        {
            throw new InvalidOperationException("EmailSettings:Sender must be configured.");
        }

        if (string.IsNullOrWhiteSpace(_emailSettings.Password))
        {
            throw new InvalidOperationException("EmailSettings:Password must be configured.");
        }
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

        var senderName = string.IsNullOrWhiteSpace(_emailSettings.SenderName)
            ? "Mohamed Transit Group"
            : _emailSettings.SenderName;

        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(senderName, _emailSettings.Sender.Trim()));
        AddRecipients(toAddress, email.To);
        AddRecipients(ccAddress, email.Cc);

        if (email.To.Count == 0 && email.Cc.Count == 0)
        {
            throw new ArgumentException("At least one valid To or Cc recipient is required.", nameof(toAddress));
        }

        email.Subject = subject;
        var bodyBuilder = new BodyBuilder { HtmlBody = message };

        if (attachements is not null)
        {
            foreach (var attachment in attachements)
            {
                if (!string.IsNullOrWhiteSpace(attachment))
                {
                    bodyBuilder.Attachments.Add(attachment);
                }
            }
        }

        email.Body = bodyBuilder.ToMessageBody();

        // ፖርቱን መሰረት አድርጎ ትክክለኛውን SSL/TLS አማራጭ መምረጥ
        SecureSocketOptions secureSocketOptions;
        if (_emailSettings.MailPort == 465)
        {
            secureSocketOptions = SecureSocketOptions.SslOnConnect;
        }
        else if (_emailSettings.MailPort == 587)
        {
            secureSocketOptions = SecureSocketOptions.StartTls;
        }
        else
        {
            secureSocketOptions = SecureSocketOptions.Auto;
        }

        try
        {
            using var smtp = new SmtpClient
            {
                Timeout = 25000,
                CheckCertificateRevocation = false
            };

            await smtp.ConnectAsync(
                _emailSettings.MailServer,
                _emailSettings.MailPort,
                secureSocketOptions,
                cancellationToken);

            await smtp.AuthenticateAsync(
                _emailSettings.Sender.Trim(),
                _emailSettings.Password,
                cancellationToken);

            await smtp.SendAsync(email, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to send email with subject {Subject} to {RecipientCount} recipient(s). Error: {Message}",
                subject,
                email.To.Count + email.Cc.Count,
                exception.Message);
            throw;
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