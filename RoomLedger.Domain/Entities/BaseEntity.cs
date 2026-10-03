using RoomLedger.Domain.Common;

namespace RoomLedger.Domain.Entities;

public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = IndianTime.Now;
}
