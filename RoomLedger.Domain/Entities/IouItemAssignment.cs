using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class IouItemAssignment   // no CreatedAt — matches SQL table
{
    public int Id { get; set; }
    public int IouItemId { get; set; }
    public int UserId { get; set; }

    [ForeignKey("IouItemId")]
    public IouItem IouItem { get; set; } = default!;

    [ForeignKey("UserId")]
    public User User { get; set; } = default!;
}
