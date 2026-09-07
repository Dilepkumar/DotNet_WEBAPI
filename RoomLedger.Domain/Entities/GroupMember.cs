using RoomLedger.Domain.Common;
namespace RoomLedger.Domain.Entities;

public class GroupMember : BaseEntity
{
    public int GroupId { get; set; }
    public int UserId { get; set; }
    public MemberRole Role { get; set; } = MemberRole.Member;
    public MemberStatus Status { get; set; } = MemberStatus.Active;
    public decimal MonthlyPoolShare { get; set; }
    // GroupMember — ADD
    public string? AliasName { get; set; }        // for non-app roommates
    public bool? IsAlias { get; set; }             // true = not a real user
}
