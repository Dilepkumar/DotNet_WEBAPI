namespace RoomLedger.Domain.Entities;

public class PoolExpense : BaseEntity
{
    public int GroupId { get; set; }
    public int RecordedByUserId { get; set; }
    public string Description { get; set; } = default!;
    public decimal TotalAmount { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public bool IsVoided { get; set; }
    public ICollection<ExpenseItem> Items { get; set; } = new List<ExpenseItem>();
}
