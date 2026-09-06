using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
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

        private int _me => int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);
    }
}
