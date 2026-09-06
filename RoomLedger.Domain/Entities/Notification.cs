namespace RoomLedger.Domain.Entities;

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? GroupId { get; set; }
    public string Title { get; set; } = default!;
    public string Message { get; set; } = default!;
    public string Type { get; set; } = default!;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public User User { get; set; } = default!;
}