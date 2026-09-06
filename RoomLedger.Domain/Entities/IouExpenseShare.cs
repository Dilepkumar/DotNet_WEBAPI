namespace RoomLedger.Domain.Entities;

public class IouExpenseShare   // no CreatedAt — matches SQL table
{
    public int Id { get; set; }
    public int IouExpenseId { get; set; }
    public int UserId { get; set; }
}
