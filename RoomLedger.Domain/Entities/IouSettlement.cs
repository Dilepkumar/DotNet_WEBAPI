using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class IouSettlement : BaseEntity
{
    public int GroupId { get; set; }
    public int PayerId { get; set; }
    public int PayeeId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly SettledOn { get; set; }
    public string? TransactionRef { get; set; }

    [ForeignKey("PayerId")]
    public User Payer { get; set; } = default!;

    [ForeignKey("PayeeId")]
    public User Payee { get; set; } = default!;
}
