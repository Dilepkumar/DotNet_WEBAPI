using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.Infrastructure.Services;

// Dev version: logs to console so you can grab OTPs while testing.
// Swap the body for real SMTP (MailKit) before production.
public class SmtpEmailService : IEmailService
{
    public Task SendAsync(string toEmail, string subject, string body)
    {
        Console.WriteLine($"📧 EMAIL → {toEmail} | {subject} | {body}");
        return Task.CompletedTask;
    }
}
