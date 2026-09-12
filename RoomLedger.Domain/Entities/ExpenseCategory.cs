namespace RoomLedger.Domain.Entities;

public class ExpenseCategory : BaseEntity
{
    public string Name { get; set; } = default!;
    public string? Icon { get; set; }
}
