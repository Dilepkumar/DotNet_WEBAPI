namespace RoomLedger.Application.Common.Models;

public class NormalizedElectricityBillResult
{
    public bool BillGenerated { get; set; }
    public string Status { get; set; } = "MONITORING"; // MONITORING, NO_BILL, BILL_GENERATED, INVALID_CONSUMER, PROVIDER_ERROR
    public string BillerId { get; set; } = "TGSPDCL00TEL01";
    public string BillerName { get; set; } = "Southern Power Distribution Company of Telangana Ltd (TGSPDCL)";
    public string ConsumerNumber { get; set; } = string.Empty;
    public string? UniqueServiceNumber { get; set; }
    public string? District { get; set; }
    public string? ERO { get; set; }
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
    public string? RawResponse { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class ElectricityBillStatus
{
    public const string Monitoring = "MONITORING";
    public const string NoBill = "NO_BILL";
    public const string BillGenerated = "BILL_GENERATED";
    public const string InvalidConsumer = "INVALID_CONSUMER";
    public const string ProviderError = "PROVIDER_ERROR";
}
