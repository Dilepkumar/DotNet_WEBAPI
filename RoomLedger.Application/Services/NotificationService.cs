using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class NotificationService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _me;
    public NotificationService(IApplicationDbContext db, ICurrentUserService me)
    { _db = db; _me = me; }

    public async Task PushAsync(int userId, int? groupId, string title, string message, string type)
    {
        _db.Notifications.Add(new Notification
        { UserId = userId, GroupId = groupId, Title = title, Message = message, Type = type });
        await _db.SaveChangesAsync();
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
