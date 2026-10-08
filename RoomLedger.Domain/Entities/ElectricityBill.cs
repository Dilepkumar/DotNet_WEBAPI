using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class ElectricityBill : BaseEntity
{
    [NotMapped]
    public int ElectricityBillId
    {
        get => Id;
        set => Id = value;
    }

    public int ElectricityAccountId { get; set; }
    public int GroupId { get; set; }

    public string BillerId { get; set; } = default!;
    public string BillerName { get; set; } = default!;
    public string ConsumerNumber { get; set; } = default!;
    public string? CustomerName { get; set; }

    public string? BillNumber { get; set; }
    public DateTime? BillDate { get; set; }
    public string? BillPeriod { get; set; }
    public DateTime? DueDate { get; set; }

    public decimal BillAmount { get; set; }
    public decimal ACDAmount { get; set; }
    public decimal Arrears { get; set; }
    public decimal LateFee { get; set; }
    public decimal TotalAmount { get; set; }

    public string? ProviderReference { get; set; }
    public string? RawProviderResponse { get; set; }

    public string FetchSource { get; set; } = "MANUAL"; // MANUAL or BACKGROUND_JOB
    public bool IsSplitCreated { get; set; } = false;
    public int? RecurringBillId { get; set; }
    public bool IsPaidAtProvider { get; set; } = false;
    public DateTime? PaidAtProviderDate { get; set; }

    [ForeignKey("ElectricityAccountId")]
    public ElectricityAccount ElectricityAccount { get; set; } = default!;

    [ForeignKey("GroupId")]
    public Group Group { get; set; } = default!;

    [ForeignKey("RecurringBillId")]
    public RecurringBill? RecurringBill { get; set; }
}
