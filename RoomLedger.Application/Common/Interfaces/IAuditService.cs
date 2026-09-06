namespace RoomLedger.Application.Common.Interfaces;

public interface IAuditService
{
    Task LogAsync(string entityName, int entityId, string action,
                  object? oldValue, object? newValue, int modifiedBy, string? reason);
}
