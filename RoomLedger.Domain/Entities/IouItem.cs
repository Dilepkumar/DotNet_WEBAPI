using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class IouItem
{
    public int Id { get; set; }
    public int IouExpenseId { get; set; }
    public string ItemName { get; set; } = default!;
    public decimal Amount { get; set; }

    [ForeignKey("IouExpenseId")]
    public IouExpense IouExpense { get; set; } = default!;
    public ICollection<IouItemAssignment> Assignments { get; set; } = new List<IouItemAssignment>();
}
