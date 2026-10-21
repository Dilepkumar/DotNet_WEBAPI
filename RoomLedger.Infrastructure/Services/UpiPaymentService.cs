using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Infrastructure.Services;

public class UpiPaymentService : IPaymentService, IYoupiPaymentService
{
    private readonly IApplicationDbContext _db;
    private readonly ILogger<UpiPaymentService> _logger;
    private readonly IPushNotificationService _push;

    public UpiPaymentService(IApplicationDbContext db, ILogger<UpiPaymentService> logger, IPushNotificationService push)
    {
        _db = db;
        _logger = logger;
        _push = push;
    }

    public async Task<(bool ok, string message, PaymentSessionResponseDto? session)> CreatePaymentSessionAsync(int userId, CreatePaymentSessionRequestDto dto, CancellationToken ct = default)
    {
        if (dto.Amount <= 0)
            return (false, "Payment amount must be greater than zero", null);

        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            return (false, "User not found", null);

        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == dto.GroupId && m.UserId == userId && m.Status == MemberStatus.Active, ct);
        if (!isMember)
            return (false, "You are not an active member of this group", null);

        var prefix = dto.Purpose switch
        {
            PaymentPurpose.PoolContribution => "POOL",
            PaymentPurpose.BillSplit => "BILL",
            PaymentPurpose.ElectricitySplit => "ELEC",
            PaymentPurpose.IouSettlement => "IOU",
            _ => "PAY"
        };
        var uniqueSuffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var orderId = $"RL_{prefix}_{dto.GroupId}_{userId}_{uniqueSuffix}";
        var amountPaise = (long)Math.Round(dto.Amount * 100m, MidpointRounding.AwayFromZero);

        // Save transaction to database immediately (No external gateway call)
        var transaction = new PaymentTransaction
        {
            GroupId = dto.GroupId,
            PayerUserId = userId,
            PayerName = user.FullName,
            PayerPhone = user.Phone,
            Amount = dto.Amount,
            AmountPaise = amountPaise,
            Purpose = dto.Purpose,
            ReferenceId = dto.ReferenceId,
            YoupiOrderId = orderId,
            YoupiPaymentId = null,
            Status = PaymentStatus.Pending,
            CheckoutUrl = "",
            CreatedAt = IndianTime.Now
        };

        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);

        var sessionResponse = new PaymentSessionResponseDto(
            transaction.Id,
            orderId,
            dto.Amount,
            amountPaise,
            "",
            transaction.Status.ToString(),
            dto.Purpose.ToString(),
            transaction.CreatedAt
        );

        return (true, "Payment session created successfully", sessionResponse);
    }

    public async Task<(bool ok, string message, PaymentTransactionDto? transaction)> GetStatusAsync(int userId, string orderId, CancellationToken ct = default)
    {
        var tx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .FirstOrDefaultAsync(t => t.YoupiOrderId == orderId, ct);

        if (tx == null)
            return (false, "Payment transaction not found", null);

        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == tx.GroupId && m.UserId == userId && m.Status == MemberStatus.Active, ct);
        if (!isMember)
            return (false, "Unauthorized access to transaction", null);

        return (true, "Status fetched", MapToDto(tx));
    }

    public async Task<(bool ok, string message, PaymentTransactionDto? transaction)> ConfirmPaymentAsync(int userId, ConfirmPaymentRequestDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.OrderId))
            return (false, "Order ID is required", null);

        var tx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Include(t => t.Group)
            .FirstOrDefaultAsync(t => t.YoupiOrderId == dto.OrderId, ct);

        if (tx == null)
            return (false, "Transaction not found", null);

        var isPayer = tx.PayerUserId == userId;
        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == tx.GroupId && m.UserId == userId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active, ct);

        if (!isPayer && !isAdmin)
            return (false, "You are not authorized to confirm this transaction", null);

        if (tx.Status == PaymentStatus.Success)
            return (true, "Payment is already marked as verified", MapToDto(tx));

        if (!string.IsNullOrWhiteSpace(dto.BankUtr))
            tx.BankUtr = dto.BankUtr.Trim();
        else if (string.IsNullOrWhiteSpace(tx.BankUtr))
            tx.BankUtr = "Direct UPI";

        if (!string.IsNullOrWhiteSpace(dto.PaymentApp))
            tx.PaymentApp = dto.PaymentApp.Trim();

        if (!string.IsNullOrWhiteSpace(dto.PayerVpa))
            tx.PayerVpa = dto.PayerVpa.Trim();

        if (!string.IsNullOrWhiteSpace(dto.PayerName))
            tx.PayerName = dto.PayerName.Trim();
        else if (string.IsNullOrWhiteSpace(tx.PayerName))
            tx.PayerName = tx.PayerUser.FullName;

        if (!string.IsNullOrWhiteSpace(dto.PayerPhone))
            tx.PayerPhone = dto.PayerPhone.Trim();
        else if (string.IsNullOrWhiteSpace(tx.PayerPhone))
            tx.PayerPhone = tx.PayerUser.Phone;

        var purposeTitle = tx.Purpose switch
        {
            PaymentPurpose.PoolContribution => "Daily Pool",
            PaymentPurpose.BillSplit => "Fixed Bill Share",
            PaymentPurpose.ElectricitySplit => "Electricity Share",
            PaymentPurpose.IouSettlement => "IOU Settle-Up",
            _ => "Expense"
        };
        var appInfo = !string.IsNullOrWhiteSpace(tx.PaymentApp) ? $" via {tx.PaymentApp}" : "";
        var utrInfo = !string.IsNullOrWhiteSpace(tx.BankUtr) && tx.BankUtr != "Direct UPI" ? $" (UTR: {tx.BankUtr})" : "";

        // Direct Settlement:
        // 1. Friend-to-friend IOU Settle-Up: settles debt directly between the two flatmates.
        // 2. Admin paying their own share: no self-approval needed.
        if (tx.Purpose == PaymentPurpose.IouSettlement || (isPayer && isAdmin))
        {
            tx.Status = PaymentStatus.Success;
            tx.VerifiedAt = IndianTime.Now;

            await ApplyDomainFulfillmentAsync(tx, ct);

            try
            {
                if (tx.Purpose == PaymentPurpose.IouSettlement && tx.ReferenceId.HasValue)
                {
                    await _push.SendPushToUsersAsync(
                        new List<int> { tx.ReferenceId.Value },
                        "🎉 IOU Settle-Up Received",
                        $"{tx.PayerName ?? tx.PayerUser.FullName} settled ₹{tx.Amount:N0} with you{appInfo}{utrInfo}.",
                        "/iou"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send payee notification for IOU settlement {OrderId}", tx.YoupiOrderId);
            }

            await _db.SaveChangesAsync(ct);
            return (true, $"Payment verified & settled successfully (UTR: {tx.BankUtr}).", MapToDto(tx));
        }

        // Shared expense verification (Pool / Bills / Electricity paid by member to Admin):
        // Mark as Processing (Awaiting Group Admin Bank Statement Check)
        tx.Status = PaymentStatus.Processing;
        tx.VerifiedAt = null;

        try
        {
            var adminIds = await _db.GroupMembers
                .Where(m => m.GroupId == tx.GroupId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active && m.UserId != tx.PayerUserId)
                .Select(m => m.UserId)
                .ToListAsync(ct);

            if (adminIds.Count > 0)
            {
                await _push.SendPushToUsersAsync(
                    adminIds,
                    "🔔 Payment Verification Request",
                    $"{tx.PayerName ?? tx.PayerUser.FullName} submitted ₹{tx.Amount:N0} for {purposeTitle}{appInfo}{utrInfo}. Please check your bank app and approve.",
                    "/bills"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin notification for payment submission {OrderId}", tx.YoupiOrderId);
        }

        await _db.SaveChangesAsync(ct);
        return (true, $"Payment proof submitted (UTR: {tx.BankUtr}). Awaiting Group Admin verification.", MapToDto(tx));
    }

    public async Task<List<PaymentTransactionDto>> GetPendingApprovalsAsync(int adminUserId, int groupId, CancellationToken ct = default)
    {
        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == adminUserId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active, ct);
        if (!isAdmin)
            return new List<PaymentTransactionDto>();

        var list = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Where(t => t.GroupId == groupId && t.Status == PaymentStatus.Processing)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return list.Select(MapToDto).ToList();
    }

    public async Task<(bool ok, string message, PaymentTransactionDto? transaction)> ApprovePaymentAsync(int adminUserId, ApprovePaymentRequestDto dto, CancellationToken ct = default)
    {
        var tx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Include(t => t.Group)
            .FirstOrDefaultAsync(t => t.Id == dto.TransactionId, ct);

        if (tx == null)
            return (false, "Transaction not found", null);

        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == tx.GroupId && m.UserId == adminUserId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active, ct);
        if (!isAdmin)
            return (false, "Only group Admins can approve payment claims", null);

        if (tx.Status == PaymentStatus.Success)
            return (true, "Payment is already marked as verified", MapToDto(tx));

        if (dto.Approve)
        {
            tx.Status = PaymentStatus.Success;
            tx.VerifiedAt = IndianTime.Now;

            await ApplyDomainFulfillmentAsync(tx, ct);

            try
            {
                await _push.SendPushToUsersAsync(
                    new List<int> { tx.PayerUserId },
                    "✅ Payment Verified & Approved",
                    $"Your payment of ₹{tx.Amount:N0} for {tx.Purpose} has been verified and approved by Admin.",
                    "/bills"
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send payer push notification for approved payment {OrderId}", tx.YoupiOrderId);
            }

            await _db.SaveChangesAsync(ct);
            return (true, "Payment approved and recorded!", MapToDto(tx));
        }
        else
        {
            tx.Status = PaymentStatus.Cancelled;
            tx.FailureReason = dto.RejectionReason ?? "Rejected by admin";

            try
            {
                await _push.SendPushToUsersAsync(
                    new List<int> { tx.PayerUserId },
                    "❌ Payment Claim Not Verified",
                    $"Your payment claim of ₹{tx.Amount:N0} was rejected by Admin: {tx.FailureReason}",
                    "/bills"
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send payer rejection notification for {OrderId}", tx.YoupiOrderId);
            }

            await _db.SaveChangesAsync(ct);
            return (true, "Payment claim rejected", MapToDto(tx));
        }
    }

    private static PaymentTransactionDto MapToDto(PaymentTransaction tx) => new(
        tx.Id,
        tx.GroupId,
        tx.PayerUserId,
        tx.PayerName ?? tx.PayerUser?.FullName ?? "Unknown",
        tx.Amount,
        tx.Purpose.ToString(),
        tx.ReferenceId,
        tx.YoupiOrderId,
        tx.Status.ToString(),
        tx.BankUtr,
        tx.PaymentApp,
        tx.PayerVpa,
        tx.PayerPhone ?? tx.PayerUser?.Phone,
        tx.VerifiedAt,
        tx.CreatedAt
    );

    private async Task ApplyDomainFulfillmentAsync(PaymentTransaction tx, CancellationToken ct)
    {
        switch (tx.Purpose)
        {
            case PaymentPurpose.PoolContribution:
                var contribution = new PoolContribution
                {
                    GroupId = tx.GroupId,
                    UserId = tx.PayerUserId,
                    Amount = tx.Amount,
                    ContributedOn = IndianTime.Today,
                    PeriodMonth = IndianTime.CurrentMonth,
                    TransactionRef = tx.BankUtr ?? tx.YoupiOrderId,
                    Message = $"Direct UPI (UTR: {tx.BankUtr})",
                    Status = ContributionStatus.Approved,
                    ApprovedByUserId = tx.PayerUserId,
                    ApprovedAt = IndianTime.Now
                };
                _db.PoolContributions.Add(contribution);
                break;

            case PaymentPurpose.BillSplit:
                if (tx.ReferenceId.HasValue)
                {
                    var split = await _db.BillSplits.FindAsync(new object[] { tx.ReferenceId.Value }, ct);
                    if (split != null && !split.IsPaid)
                    {
                        split.IsPaid = true;
                        split.PaidAt = IndianTime.Now;
                    }
                }
                break;

            case PaymentPurpose.ElectricitySplit:
                if (tx.ReferenceId.HasValue)
                {
                    var elecSplit = await _db.BillSplits.FindAsync(new object[] { tx.ReferenceId.Value }, ct);
                    if (elecSplit != null && !elecSplit.IsPaid)
                    {
                        elecSplit.IsPaid = true;
                        elecSplit.PaidAt = IndianTime.Now;
                    }
                }
                break;

            case PaymentPurpose.IouSettlement:
                if (tx.ReferenceId.HasValue)
                {
                    var settlement = new IouSettlement
                    {
                        GroupId = tx.GroupId,
                        PayerId = tx.PayerUserId,
                        PayeeId = tx.ReferenceId.Value,
                        Amount = tx.Amount,
                        SettledOn = IndianTime.Today,
                        TransactionRef = tx.BankUtr ?? tx.YoupiOrderId
                    };
                    _db.IouSettlements.Add(settlement);
                }
                break;
        }
    }
}
