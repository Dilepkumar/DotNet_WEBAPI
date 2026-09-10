namespace RoomLedger.Domain.Entities;

public class PoolExpense : BaseEntity
{
    public int GroupId { get; set; }
    public int RecordedByUserId { get; set; }
    public int? PaidByUserId { get; set; }             // null = Paid directly from Central Pool, or userId = Member who paid out of pocket
    public string? PayerName { get; set; }              // Snapshot name of the payer ("Central Pool" or member's name)
    public string Description { get; set; } = default!;
    public decimal TotalAmount { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public bool IsVoided { get; set; }
    public string? ReceiptUrl { get; set; }             // Uploaded receipt/bill image URL
    public string? Category { get; set; }               // Dynamic category name
    public bool IsReimbursed { get; set; } = true;      // True if paid from pool or reimbursed/settled from pool
    public ICollection<ExpenseItem> Items { get; set; } = new List<ExpenseItem>();
}
