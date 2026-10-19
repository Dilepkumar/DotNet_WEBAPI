using RoomLedger.Domain.Common;
using System.ComponentModel.DataAnnotations.Schema;

namespace RoomLedger.Domain.Entities;

public class PaymentTransaction : BaseEntity
{
    public int GroupId { get; set; }
    public int PayerUserId { get; set; }
    public decimal Amount { get; set; }
    public long AmountPaise { get; set; }
    public PaymentPurpose Purpose { get; set; }
    public int? ReferenceId { get; set; }
    public string YoupiOrderId { get; set; } = default!;
    public string? YoupiPaymentId { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? BankUtr { get; set; }
    public string? PaymentApp { get; set; }
    public string? PayerVpa { get; set; }
    public string? PayerName { get; set; }
    public string? PayerPhone { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? FailureReason { get; set; }
    public string? CheckoutUrl { get; set; }

    [ForeignKey("GroupId")]
    public Group Group { get; set; } = default!;

    [ForeignKey("PayerUserId")]
    public User PayerUser { get; set; } = default!;
}
