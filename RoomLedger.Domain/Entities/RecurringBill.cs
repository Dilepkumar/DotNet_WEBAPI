namespace RoomLedger.Domain.Entities;

public class RecurringBill : BaseEntity
{
    public int GroupId { get; set; }
    public string BillName { get; set; } = default!;
    public decimal Amount { get; set; }
    public int DueDayOfMonth { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateOnly NextBillingMonth { get; set; }
    public bool PaidFromPool { get; set; } = false;
    public ICollection<BillSplit> Splits { get; set; } = new List<BillSplit>();
}
