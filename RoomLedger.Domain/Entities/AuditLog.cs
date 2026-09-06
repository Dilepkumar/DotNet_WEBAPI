namespace RoomLedger.Domain.Entities;

public class AuditLog : BaseEntity
{
    public string EntityName { get; set; } = default!;
    public int EntityId { get; set; }
    public string Action { get; set; } = default!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public int ModifiedByUserId { get; set; }
    public string? Reason { get; set; }
}
