using RoomLedger.Domain.Common;

namespace RoomLedger.Domain.Entities;

public class User : BaseEntity
{
    public string FullName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = default!;
    public bool IsEmailVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DateOfBirth { get; set; }
    public Gender? Gender { get; set; }
    public string? AvatarUrl { get; set; }
    public ICollection<Settlement> SettlementsSent { get; set; } = new List<Settlement>();
    public ICollection<Settlement> SettlementsReceived { get; set; } = new List<Settlement>();
}
