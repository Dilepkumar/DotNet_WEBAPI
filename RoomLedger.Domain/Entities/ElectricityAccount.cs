using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class ElectricityAccount : BaseEntity
{
    [NotMapped]
    public int ElectricityAccountId
    {
        get => Id;
        set => Id = value;
    }

    public int GroupId { get; set; }
    public string BillerId { get; set; } = default!;
    public string BillerName { get; set; } = default!;
    public string ConsumerNumber { get; set; } = default!;
    public string CustomerParametersJson { get; set; } = "{}";
    public string? CustomerName { get; set; }
    public int CreatedByUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public int? ExpectedBillDayOfMonth { get; set; }
    public DateTime? EstimatedNextBillDate { get; set; }
    public string MonitoringStatus { get; set; } = "MONITORING";

    public DateTime? LastCheckedAt { get; set; }
    public DateTime? NextCheckAt { get; set; }
    public string? LastCheckStatus { get; set; }
    public string? LastCheckMessage { get; set; }
    public int ManualChecksTodayCount { get; set; } = 0;
    public DateOnly? LastManualCheckDate { get; set; }

    [ForeignKey("GroupId")]
    public Group Group { get; set; } = default!;

    [ForeignKey("CreatedByUserId")]
    public User CreatedByUser { get; set; } = default!;

    public ICollection<ElectricityBill> Bills { get; set; } = new List<ElectricityBill>();
    public ICollection<ElectricityCheckLog> CheckLogs { get; set; } = new List<ElectricityCheckLog>();
}
