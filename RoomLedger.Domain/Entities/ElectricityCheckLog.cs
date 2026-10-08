using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class ElectricityCheckLog : BaseEntity
{
    [NotMapped]
    public int ElectricityCheckLogId
    {
        get => Id;
        set => Id = value;
    }

    public int ElectricityAccountId { get; set; }
    public int? CheckedByUserId { get; set; }
    public string CheckType { get; set; } = "MANUAL";

    public string Status { get; set; } = default!;

    public string? Message { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? RawResponse { get; set; }

    [ForeignKey("ElectricityAccountId")]
    public ElectricityAccount ElectricityAccount { get; set; } = default!;

    [ForeignKey("CheckedByUserId")]
    public User? CheckedByUser { get; set; }
}
