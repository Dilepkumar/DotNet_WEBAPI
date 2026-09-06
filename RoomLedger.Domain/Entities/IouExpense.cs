namespace RoomLedger.Domain.Entities;

public class IouExpense : BaseEntity
{
    public int GroupId { get; set; }
    public int PaidById { get; set; }
    public string Description { get; set; } = default!;
    public decimal Amount { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public bool IsVoided { get; set; }
    public ICollection<IouExpenseShare> Shares { get; set; } = new List<IouExpenseShare>();
    public ICollection<IouParticipant> Participants { get; set; } = new List<IouParticipant>();
    public ICollection<IouItem> Items { get; set; } = new List<IouItem>();
}
