using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class NotificationService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _me;
    private readonly IPushNotificationService _push;

    public NotificationService(
        IApplicationDbContext db, 
        ICurrentUserService me,
        IPushNotificationService push)
    { 
        _db = db; 
        _me = me;
        _push = push;
    }

    public async Task PushAsync(int userId, int? groupId, string title, string message, string type, string? customTargetUrl = null)
    {
        _db.Notifications.Add(new Notification
        { UserId = userId, GroupId = groupId, Title = title, Message = message, Type = type });
        await _db.SaveChangesAsync();

        try
        {
            var targetUrl = !string.IsNullOrWhiteSpace(customTargetUrl)
                ? customTargetUrl
                : (groupId.HasValue ? $"/g/{groupId.Value}/dashboard" : "/notifications");
            await _push.SendPushNotificationAsync(userId, title, message, targetUrl);
        }
        catch
        {
            // Web push failure should never abort the in-app notification flow
        }
    }

    public async Task<List<NotificationDto>> GetMineAsync()
    {
        return await _db.Notifications
            .Where(n => n.UserId == _me.UserId)
            .OrderByDescending(n => n.CreatedAt).Take(50)
            .Select(n => new NotificationDto(n.Id, n.Title, n.Message, n.Type, n.IsRead, n.CreatedAt))
            .ToListAsync();
    }

    public async Task MarkAllReadAsync()
    {
        var unread = await _db.Notifications
            .Where(n => n.UserId == _me.UserId && !n.IsRead).ToListAsync();
        foreach (var n in unread) n.IsRead = true;
        await _db.SaveChangesAsync();
    }
}
