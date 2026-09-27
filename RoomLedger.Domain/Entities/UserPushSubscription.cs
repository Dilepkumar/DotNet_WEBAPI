namespace RoomLedger.Domain.Entities;

public class UserPushSubscription : BaseEntity
{
    public int UserId { get; set; }
    public string Endpoint { get; set; } = default!;
    public string P256dh { get; set; } = default!;
    public string Auth { get; set; } = default!;
}
