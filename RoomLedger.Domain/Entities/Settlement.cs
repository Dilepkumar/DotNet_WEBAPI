namespace RoomLedger.Domain.Entities;

public class Settlement : BaseEntity
{
    public int GroupId { get; set; }
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly SettledOn { get; set; }
    public string? Note { get; set; }
}
