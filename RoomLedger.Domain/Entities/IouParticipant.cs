using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class IouParticipant   // no CreatedAt — matches SQL
{
    public int Id { get; set; }
    public int IouExpenseId { get; set; }
    public int UserId { get; set; }
    public decimal? ShareAmount { get; set; }   // null = equal share
    [ForeignKey("IouExpenseId")]
    public IouExpense IouExpense { get; set; } = default!;
}
