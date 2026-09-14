using System.Text.Json;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class AuditService : IAuditService
{
    private readonly IApplicationDbContext _db;
    public AuditService(IApplicationDbContext db) => _db = db;

    public async Task LogAsync(string entityName, int entityId, string action, object? oldValue, object? newValue, int modifiedBy, string? reason)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityName = entityName,
            EntityId = entityId,
            Action = action,
            OldValue = oldValue == null ? null : JsonSerializer.Serialize(oldValue),
            NewValue = newValue == null ? null : JsonSerializer.Serialize(newValue),
            ModifiedByUserId = modifiedBy,
            Reason = reason
        });
        await _db.SaveChangesAsync();
    }
}
