using RoomLedger.Domain.Common;
namespace RoomLedger.Domain.Entities;

public class OtpCode : BaseEntity
{
    public string Email { get; set; } = default!;
    public string Code { get; set; } = default!;
    public OtpPurpose Purpose { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
}
