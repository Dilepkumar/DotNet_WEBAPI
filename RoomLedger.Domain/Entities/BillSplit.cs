using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class BillSplit : BaseEntity
{
    public int RecurringBillId { get; set; }
    public int GroupId { get; set; }
    public string BillingMonth { get; set; } = default!;
    public int UserId { get; set; }
    public decimal ShareAmount { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }

    [ForeignKey("RecurringBillId")]
    public RecurringBill RecurringBill { get; set; } = default!;

    [ForeignKey("UserId")]
    public User User { get; set; } = default!;
}
