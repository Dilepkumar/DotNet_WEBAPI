namespace RoomLedger.Application.Common.Interfaces;

public interface IEmailTemplateService
{
    Task<bool> SendTemplatedEmailAsync(string toEmail, string templateKey, IDictionary<string, string> placeholders);
}
