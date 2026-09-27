using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;
using System.Security.Claims;

namespace RoomLedger.API.Controllers
{
    [ApiController]
    [Route("api/notifications")]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly IApplicationDbContext _db;
        private readonly NotificationService _notifications;

        public NotificationsController(IApplicationDbContext db, NotificationService notifications)
        { _db = db; _notifications = notifications; }

        [HttpGet]
        public async Task<IActionResult> GetAll()
            => Ok(await _notifications.GetMineAsync());

        [HttpGet("unread-count")]
        public async Task<IActionResult> UnreadCount()
            => Ok(new
            {
                count = await _db.Notifications
                    .CountAsync(n => n.UserId == _me && !n.IsRead)
            });

        [HttpPost("mark-read")]
        public async Task<IActionResult> MarkRead()
        {
            await _notifications.MarkAllReadAsync();
            return Ok(new { message = "All notifications marked read" });
        }

        [HttpPost("push/subscribe")]
        public async Task<IActionResult> SubscribePush([FromBody] PushSubscriptionDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Endpoint) || dto.Keys == null)
                return BadRequest(new { message = "Invalid subscription payload" });

            var existing = await _db.UserPushSubscriptions
                .FirstOrDefaultAsync(s => s.UserId == _me && s.Endpoint == dto.Endpoint);

            if (existing == null)
            {
                _db.UserPushSubscriptions.Add(new Domain.Entities.UserPushSubscription
                {
                    UserId = _me,
                    Endpoint = dto.Endpoint,
                    P256dh = dto.Keys.P256dh,
                    Auth = dto.Keys.Auth
                });
            }
            else
            {
                existing.P256dh = dto.Keys.P256dh;
                existing.Auth = dto.Keys.Auth;
            }

            await _db.SaveChangesAsync();
            return Ok(new { message = "Device successfully registered for push alerts!" });
        }

        [HttpPost("push/unsubscribe")]
        public async Task<IActionResult> UnsubscribePush([FromBody] PushSubscriptionDto dto)
        {
            var existing = await _db.UserPushSubscriptions
                .FirstOrDefaultAsync(s => s.UserId == _me && s.Endpoint == dto.Endpoint);
            if (existing != null)
            {
                _db.UserPushSubscriptions.Remove(existing);
                await _db.SaveChangesAsync();
            }
            return Ok(new { message = "Device unsubscribed from push alerts." });
        }

        [HttpPost("push/test")]
        public async Task<IActionResult> TestPush([FromServices] IPushNotificationService push)
        {
            await push.SendPushNotificationAsync(_me, "RoomLedger Test Alert 🔔", "Web Push is working seamlessly on your device!");
            return Ok(new { message = "Test notification sent to your device." });
        }
        private int _me => int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);
    }
}
