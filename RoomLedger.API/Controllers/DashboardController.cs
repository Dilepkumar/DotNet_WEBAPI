using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.Services;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups/{groupId:int}/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly DashboardService _dashboard;
    private readonly NotificationService _notifications;
    public DashboardController(DashboardService dashboard, NotificationService notifications)
    { _dashboard = dashboard; _notifications = notifications; }

    [HttpGet]
    public async Task<IActionResult> Get(int groupId)
        => Ok(await _dashboard.GetAsync(groupId));

    [HttpGet("notifications")]       // GET api/groups/{id}/dashboard/notifications
    public async Task<IActionResult> Notifications()
        => Ok(await _notifications.GetMineAsync());

    [HttpPost("notifications/mark-read")]
    public async Task<IActionResult> MarkRead()
    { await _notifications.MarkAllReadAsync(); return Ok(new { message = "All read" }); }
}
