using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Infrastructure.Configuration;

namespace RoomLedger.Infrastructure.Services;

public class SmtpEmailService : IEmailService
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpSettings> settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string body)
    {
        // If SMTP is not configured or uses placeholder credentials, log to console for development
        if (string.IsNullOrWhiteSpace(_settings.UserName) || string.IsNullOrWhiteSpace(_settings.Password))
        {
            _logger.LogWarning("📧 [DEV MODE] SMTP credentials not set. Email to {ToEmail} | Subject: {Subject}", toEmail, subject);
            Console.WriteLine($"📧 ================= EMAIL DEV LOG =================");
            Console.WriteLine($"To: {toEmail}");
            Console.WriteLine($"Subject: {subject}");
            Console.WriteLine($"Body:\n{body}");
            Console.WriteLine($"=====================================================");
            return;
        }

        try
        {
            var message = new MimeMessage();
            var fromEmail = !string.IsNullOrWhiteSpace(_settings.FromEmail) ? _settings.FromEmail : _settings.UserName;
            var fromName = !string.IsNullOrWhiteSpace(_settings.FromName) ? _settings.FromName : "RoomLedger";

            // From & To
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(toEmail, toEmail));
            message.ReplyTo.Add(new MailboxAddress(fromName, fromEmail));
            message.Subject = subject;

            // Unique Message-ID helps avoid spam filters
            message.MessageId = MimeUtils.GenerateMessageId();

            // X-Mailer header for identification
            message.Headers.Add("X-Mailer", "RoomLedger Mailer 1.0");

            // Both HTML + plain text fallback (reduces spam score significantly)
            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = body,
                TextBody = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]*>", string.Empty).Trim()
            };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            var secureOption = _settings.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : (_settings.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);

            await client.ConnectAsync(_settings.Host, _settings.Port, secureOption);
            await client.AuthenticateAsync(_settings.UserName, _settings.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("✅ Email successfully sent to {ToEmail} with subject '{Subject}'", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send email via SMTP to {ToEmail}. Subject: {Subject}", toEmail, subject);
            Console.WriteLine($"📧 [SMTP ERROR FALLBACK] Delivery failed: {ex.Message}");
            Console.WriteLine($"To: {toEmail} | Subject: {subject}");
            Console.WriteLine($"Body:\n{body}");
        }
    }
}
