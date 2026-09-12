namespace RoomLedger.Domain.Entities;

public class Group : BaseEntity
{
    public string GroupName { get; set; } = default!;
    public int CreatedByUserId { get; set; }
    public string? InviteCode { get; set; }
    public string? Address { get; set; }
    public decimal MonthlyPoolTarget { get; set; }
    public ICollection<GroupMember> Members { get; set; } = new List<GroupMember>();
}
