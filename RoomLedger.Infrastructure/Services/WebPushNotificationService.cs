using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Infrastructure.Configuration;
using RoomLedger.Domain.Common;
using WebPush;

namespace RoomLedger.Infrastructure.Services;

public class WebPushNotificationService : IPushNotificationService
{
    private readonly IApplicationDbContext _db;
    private readonly WebPushSettings _settings;
    private readonly ILogger<WebPushNotificationService> _logger;

    public WebPushNotificationService(IApplicationDbContext db, IOptions<WebPushSettings> options, ILogger<WebPushNotificationService> logger)
    {
        _db = db;
        _settings = options.Value;
        _logger = logger;
    }

    public async Task SendPushNotificationAsync(int userId, string title, string message, string? url = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.PublicKey) || string.IsNullOrWhiteSpace(_settings.PrivateKey))
        {
            _logger.LogWarning("VAPID keys not configured. Skipping Web Push notification.");
            return;
        }

        var subs = await _db.UserPushSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync();

        if (subs.Count == 0) return;

        var payload = JsonSerializer.Serialize(new
        {
            notification = new
            {
                title,
                body = message,
                icon = "/icons/icon-192x192.png",
                badge = "/icons/icon-72x72.png",
                vibrate = new[] { 100, 50, 100 },
                data = new
                {
                    url = url ?? "/",
                    dateOfArrival = IndianTime.Now
                },
                actions = new[]
                {
                    new { action = "open", title = "View in App" }
                }
            }
        });

        var vapid = new VapidDetails(_settings.Subject, _settings.PublicKey, _settings.PrivateKey);
        var client = new WebPushClient();
        var staleSubs = new List<Domain.Entities.UserPushSubscription>();

        foreach (var sub in subs)
        {
            try
            {
                var pushSub = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await client.SendNotificationAsync(pushSub, payload, vapid);
            }
            catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Subscription {Endpoint} has expired or was revoked. Removing.", sub.Endpoint);
                staleSubs.Add(sub);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send web push to endpoint: {Endpoint}", sub.Endpoint);
            }
        }

        if (staleSubs.Count > 0)
        {
            _db.UserPushSubscriptions.RemoveRange(staleSubs);
            await _db.SaveChangesAsync();
        }
    }

    public async Task SendPushToUsersAsync(IEnumerable<int> userIds, string title, string message, string? url = null)
    {
        var distinctIds = userIds.Distinct().ToList();
        foreach (var uid in distinctIds)
        {
            await SendPushNotificationAsync(uid, title, message, url);
        }
    }
}
