using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.DTOs;

public record CreatePaymentSessionRequestDto(
    int GroupId,
    decimal Amount,
    PaymentPurpose Purpose,
    int? ReferenceId = null,
    string? Description = null,
    string? ReturnUrl = null
);

public record PaymentSessionResponseDto(
    int PaymentTransactionId,
    string OrderId,
    decimal Amount,
    long AmountPaise,
    string CheckoutUrl,
    string Status,
    string Purpose,
    DateTime CreatedAt
);

public record PaymentTransactionDto(
    int Id,
    int GroupId,
    int PayerUserId,
    string PayerName,
    decimal Amount,
    string Purpose,
    int? ReferenceId,
    string OrderId,
    string Status,
    string? BankUtr,
    string? PaymentApp,
    string? PayerVpa,
    string? PayerPhone,
    DateTime? VerifiedAt,
    DateTime CreatedAt
);

public record ConfirmPaymentRequestDto(
    string OrderId,
    string? BankUtr = null,
    string? PaymentApp = null,
    string? PayerVpa = null,
    string? PayerPhone = null,
    string? PayerName = null
);

public record ApprovePaymentRequestDto(
    int TransactionId,
    bool Approve,
    string? RejectionReason = null
);



