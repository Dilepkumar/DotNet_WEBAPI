using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class ExpenseItem 
{
    public int Id { get; set; }
    public int PoolExpenseId { get; set; }
    public int? ExpenseCategoryId { get; set; }
    public string ItemName { get; set; } = default!;
    public decimal Amount { get; set; }
    [ForeignKey("PoolExpenseId")]
    public PoolExpense PoolExpense { get; set; } = default!;
    [ForeignKey("ExpenseCategoryId")]
    public ExpenseCategory? ExpenseCategory { get; set; }
}
