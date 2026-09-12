using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.Infrastructure.Services;

public class EmailTemplateService : IEmailTemplateService
{
    private readonly IApplicationDbContext _db;
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailTemplateService> _logger;

    public EmailTemplateService(IApplicationDbContext db, IEmailService emailService, ILogger<EmailTemplateService> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<bool> SendTemplatedEmailAsync(string toEmail, string templateKey, IDictionary<string, string> placeholders)
    {
        try
        {
            // Fetch template directly from the database table EmailTemplates
            var template = await _db.EmailTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TemplateKey == templateKey && t.IsActive && t.Status == "Active");

            if (template == null || string.IsNullOrWhiteSpace(template.HtmlBody))
            {
                _logger.LogError("Email template with key '{TemplateKey}' not found or inactive in the database table 'EmailTemplates'.", templateKey);
                return false;
            }

            var subject = template.Subject;
            var htmlBody = template.HtmlBody;

            // Replace all placeholders dynamically from database template
            foreach (var (key, value) in placeholders)
            {
                var replacement = value ?? string.Empty;
                subject = subject.Replace(key, replacement);
                htmlBody = htmlBody.Replace(key, replacement);

                var rawKey = key.Trim('{', '}');
                subject = subject.Replace($"{{{rawKey}}}", replacement);
                htmlBody = htmlBody.Replace($"{{{rawKey}}}", replacement);
            }

            await _emailService.SendAsync(toEmail, subject, htmlBody);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing templated email for key '{TemplateKey}' to {ToEmail}", templateKey, toEmail);
            return false;
        }
    }
}
