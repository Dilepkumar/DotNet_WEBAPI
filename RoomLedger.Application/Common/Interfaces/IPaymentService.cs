using RoomLedger.Application.DTOs;

namespace RoomLedger.Application.Common.Interfaces;

public interface IPaymentService
{
    Task<(bool ok, string message, PaymentSessionResponseDto? session)> CreatePaymentSessionAsync(
        int userId, 
        CreatePaymentSessionRequestDto dto, 
        CancellationToken ct = default);

    Task<(bool ok, string message, PaymentTransactionDto? transaction)> GetStatusAsync(
        int userId, 
        string orderId, 
        CancellationToken ct = default);

    Task<(bool ok, string message, PaymentTransactionDto? transaction)> ConfirmPaymentAsync(
        int userId,
        ConfirmPaymentRequestDto dto,
        CancellationToken ct = default);

    Task<List<PaymentTransactionDto>> GetPendingApprovalsAsync(
        int adminUserId,
        int groupId,
        CancellationToken ct = default);

    Task<(bool ok, string message, PaymentTransactionDto? transaction)> ApprovePaymentAsync(
        int adminUserId,
        ApprovePaymentRequestDto dto,
        CancellationToken ct = default);
}
