using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;
using RoomLedger.Infrastructure.Configuration;

namespace RoomLedger.Infrastructure.Services;

public class YoupiPaymentService : IYoupiPaymentService
{
    private readonly HttpClient _http;
    private readonly IApplicationDbContext _db;
    private readonly YoupiSettings _settings;
    private readonly ILogger<YoupiPaymentService> _logger;
    private readonly IPushNotificationService _push;

    public YoupiPaymentService(HttpClient http, IApplicationDbContext db, IOptions<YoupiSettings> options, ILogger<YoupiPaymentService> logger, IPushNotificationService push)
    {
        _http = http;
        _db = db;
        _settings = options.Value;
        _logger = logger;
        _push = push;

        if (!string.IsNullOrWhiteSpace(_settings.BaseUrl))
        {
            _http.BaseAddress = new Uri(_settings.BaseUrl);
        }
        _http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
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

        // Generate unique merchant order ID
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

        // Construct YouPI Gateway payload
        var returnUrl = !string.IsNullOrWhiteSpace(dto.ReturnUrl) 
            ? dto.ReturnUrl 
            : $"{_settings.CheckoutUrl}/order-success?order_id={orderId}";

        var payload = new
        {
            orderId,
            amount = dto.Amount.ToString("0.00"),
            currency = "INR",
            customer = new
            {
                name = user.FullName,
                email = user.Email,
                phone = user.Phone ?? ""
            },
            metadata = new Dictionary<string, string>
            {
                ["groupId"] = dto.GroupId.ToString(),
                ["payerUserId"] = userId.ToString(),
                ["purpose"] = dto.Purpose.ToString(),
                ["referenceId"] = dto.ReferenceId?.ToString() ?? "",
                ["description"] = dto.Description ?? ""
            },
            returnUrl
        };

        string checkoutUrl;
        string? youpiPaymentId = null;

        try
        {
            _logger.LogInformation("Creating YouPI PG payment order {OrderId} for ₹{Amount}...", orderId, dto.Amount);
            
            // Add custom merchant header
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Add("X-Merchant-Id", _settings.MerchantId);

            var response = await _http.SendAsync(req, ct);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                checkoutUrl = result.TryGetProperty("checkoutUrl", out var cUrl) 
                    ? cUrl.GetString() ?? $"{_settings.CheckoutUrl}/pay/{orderId}"
                    : $"{_settings.CheckoutUrl}/pay/{orderId}";

                if (result.TryGetProperty("paymentId", out var pId))
                {
                    youpiPaymentId = pId.GetString();
                }
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("YouPI PG returned {Status}: {Error}. Falling back to standard checkout URL format.", response.StatusCode, err);
                checkoutUrl = $"{_settings.CheckoutUrl}/pay/{orderId}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach YouPI PG at {BaseUrl}. Generating deterministic local checkout URL for order {OrderId}.", _settings.BaseUrl, orderId);
            checkoutUrl = $"{_settings.CheckoutUrl}/pay/{orderId}";
        }

        // Save transaction to RoomLedger database
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
            YoupiPaymentId = youpiPaymentId,
            Status = PaymentStatus.Pending,
            CheckoutUrl = checkoutUrl,
            CreatedAt = IndianTime.Now
        };

        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);

        var sessionResponse = new PaymentSessionResponseDto(
            transaction.Id,
            orderId,
            dto.Amount,
            amountPaise,
            checkoutUrl,
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

        var dto = new PaymentTransactionDto(
            tx.Id,
            tx.GroupId,
            tx.PayerUserId,
            tx.PayerName ?? tx.PayerUser.FullName,
            tx.Amount,
            tx.Purpose.ToString(),
            tx.ReferenceId,
            tx.YoupiOrderId,
            tx.Status.ToString(),
            tx.BankUtr,
            tx.PaymentApp,
            tx.PayerVpa,
            tx.PayerPhone ?? tx.PayerUser.Phone,
            tx.VerifiedAt,
            tx.CreatedAt
        );

        return (true, "Status fetched", dto);
    }

    public async Task<(bool ok, string message)> ProcessWebhookAsync(string rawBody, string signatureHeader, CancellationToken ct = default, bool bypassSignature = false)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return (false, "Empty webhook body");

        // Validate HMAC-SHA256 signature unless explicitly bypassed (e.g. local dev test simulation)
        if (!bypassSignature && !VerifyHmacSignature(rawBody, signatureHeader, _settings.WebhookSecret))
        {
            _logger.LogWarning("YouPI PG webhook HMAC signature mismatch. Header: {Signature}", signatureHeader);
            return (false, "Invalid HMAC signature");
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse YouPI webhook JSON");
            return (false, "Invalid JSON payload");
        }

        var orderId = root.TryGetProperty("orderId", out var o1) ? o1.GetString()
            : root.TryGetProperty("order_id", out var o2) ? o2.GetString() : null;

        if (string.IsNullOrWhiteSpace(orderId))
            return (false, "Missing order_id in webhook");

        var tx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Include(t => t.Group)
            .FirstOrDefaultAsync(t => t.YoupiOrderId == orderId, ct);

        if (tx == null)
        {
            _logger.LogWarning("Webhook received for unknown order: {OrderId}", orderId);
            return (false, $"Order {orderId} not found");
        }

        var statusStr = root.TryGetProperty("status", out var s) ? s.GetString()?.ToUpperInvariant() : null;
        var utr = root.TryGetProperty("utr", out var u) ? u.GetString() : null;
        var payerVpa = root.TryGetProperty("payerVpa", out var v1) ? v1.GetString()
            : root.TryGetProperty("payer_vpa", out var v2) ? v2.GetString() : null;

        _logger.LogInformation("Processing YouPI webhook for order {OrderId}. Status: {Status}, UTR: {Utr}", orderId, statusStr, utr);

        if (statusStr == "SUCCESS")
        {
            if (tx.Status == PaymentStatus.Success)
            {
                _logger.LogInformation("Order {OrderId} is already marked as SUCCESS.", orderId);
                return (true, "Already processed");
            }

            tx.Status = PaymentStatus.Success;
            tx.BankUtr = utr;
            tx.PayerVpa = payerVpa;
            tx.VerifiedAt = IndianTime.Now;

            // Fulfill domain action based on payment purpose
            await ApplyDomainFulfillmentAsync(tx, ct);

            // Send push notification to group members
            try
            {
                var purposeTitle = tx.Purpose switch
                {
                    PaymentPurpose.PoolContribution => "Daily Pool",
                    PaymentPurpose.BillSplit => "Fixed Bill Share",
                    PaymentPurpose.ElectricitySplit => "Electricity Share",
                    PaymentPurpose.IouSettlement => "IOU Settle-Up",
                    _ => "Expense"
                };

                var memberIds = await _db.GroupMembers
                    .Where(m => m.GroupId == tx.GroupId && m.Status == MemberStatus.Active && m.UserId != tx.PayerUserId)
                    .Select(m => m.UserId)
                    .ToListAsync(ct);

                if (memberIds.Count > 0)
                {
                    await _push.SendPushToUsersAsync(
                        memberIds,
                        "💰 Payment Verified via UPI",
                        $"{tx.PayerUser.FullName} paid ₹{tx.Amount:N0} for {purposeTitle} (UTR: {utr ?? "Verified"}).",
                        "/pool"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send push notification for verified payment {OrderId}", orderId);
            }
        }
        else if (statusStr == "FAILED")
        {
            tx.Status = PaymentStatus.Failed;
            tx.FailureReason = root.TryGetProperty("reason", out var r) ? r.GetString() : "Payment verification failed";
        }
        else if (statusStr == "EXPIRED")
        {
            tx.Status = PaymentStatus.Expired;
            tx.FailureReason = "Payment window expired";
        }
        else if (statusStr == "CANCELLED")
        {
            tx.Status = PaymentStatus.Cancelled;
            tx.FailureReason = "User cancelled payment";
        }

        await _db.SaveChangesAsync(ct);
        return (true, "Webhook processed successfully");
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

        // Security check: Must be payer or an admin of the group
        var isPayer = tx.PayerUserId == userId;
        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == tx.GroupId && m.UserId == userId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active, ct);

        if (!isPayer && !isAdmin)
            return (false, "You are not authorized to confirm this transaction", null);

        if (tx.Status == PaymentStatus.Success)
        {
            return (true, "Payment is already marked as verified", MapToDto(tx));
        }

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

        // Manual UTR submission -> Mark as Processing (Awaiting Group Admin Bank Statement Cross-Check)
        // DO NOT fulfill domain action yet! Bill/pool remains pending until Admin verifies in bank or Webhook succeeds!
        tx.Status = PaymentStatus.Processing;
        tx.VerifiedAt = null;

        // Send push notification to Group Admin(s)
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
                    $"{tx.PayerName ?? tx.PayerUser.FullName} submitted ₹{tx.Amount:N0} for {purposeTitle}{appInfo}{utrInfo}. Please check your bank and approve.",
                    "/bills"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin notification for payment submission {OrderId}", tx.YoupiOrderId);
        }

        await _db.SaveChangesAsync(ct);
        return (true, $"Payment proof submitted (UTR: {tx.BankUtr}). Awaiting Group Admin bank verification.", MapToDto(tx));
    }

    public async Task<List<PaymentTransactionDto>> GetPendingApprovalsAsync(int adminUserId, int groupId, CancellationToken ct = default)
    {
        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == adminUserId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active, ct);
        if (!isAdmin)
            return new List<PaymentTransactionDto>();

        var pending = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Where(t => t.GroupId == groupId && t.Status == PaymentStatus.Processing)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return pending.Select(MapToDto).ToList();
    }

    public async Task<(bool ok, string message, PaymentTransactionDto? transaction)> ApprovePaymentAsync(int adminUserId, ApprovePaymentRequestDto dto, CancellationToken ct = default)
    {
        var tx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Include(t => t.Group)
            .FirstOrDefaultAsync(t => t.Id == dto.TransactionId, ct);

        if (tx == null)
            return (false, "Payment transaction not found", null);

        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == tx.GroupId && m.UserId == adminUserId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active, ct);
        if (!isAdmin)
            return (false, "Only group admins can approve or reject payments", null);

        if (tx.Status == PaymentStatus.Success)
            return (false, "Transaction has already been approved", MapToDto(tx));

        var purposeTitle = tx.Purpose switch
        {
            PaymentPurpose.PoolContribution => "Daily Pool",
            PaymentPurpose.BillSplit => "Fixed Bill Share",
            PaymentPurpose.ElectricitySplit => "Electricity Share",
            PaymentPurpose.IouSettlement => "IOU Settle-Up",
            _ => "Expense"
        };

        if (dto.Approve)
        {
            tx.Status = PaymentStatus.Success;
            tx.VerifiedAt = IndianTime.Now;

            // Fulfill domain action now that Admin verified bank credit!
            await ApplyDomainFulfillmentAsync(tx, ct);

            // Notify payer
            try
            {
                await _push.SendPushToUsersAsync(
                    new List<int> { tx.PayerUserId },
                    "✅ Payment Approved",
                    $"Your payment of ₹{tx.Amount:N0} for {purposeTitle} was verified and approved by the Admin.",
                    "/bills"
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send approval push to payer {UserId}", tx.PayerUserId);
            }

            await _db.SaveChangesAsync(ct);
            return (true, "Payment verified & approved successfully", MapToDto(tx));
        }
        else
        {
            tx.Status = PaymentStatus.Failed;
            tx.FailureReason = !string.IsNullOrWhiteSpace(dto.RejectionReason) 
                ? dto.RejectionReason.Trim() 
                : "Rejected by Admin (Bank credit not received)";

            // Notify payer
            try
            {
                await _push.SendPushToUsersAsync(
                    new List<int> { tx.PayerUserId },
                    "❌ Payment Verification Rejected",
                    $"Your payment claim of ₹{tx.Amount:N0} for {purposeTitle} was rejected by Admin ({tx.FailureReason}).",
                    "/bills"
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send rejection push to payer {UserId}", tx.PayerUserId);
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
                    Message = $"Verified UPI via YouPI PG (UTR: {tx.BankUtr})",
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

    private static bool VerifyHmacSignature(string rawBody, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
            return false;

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
            var computedHex = Convert.ToHexString(hash).ToLowerInvariant();

            var cleanHeader = signatureHeader.Trim().ToLowerInvariant();
            if (cleanHeader.StartsWith("sha256="))
            {
                cleanHeader = cleanHeader.Substring(7);
            }

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedHex),
                Encoding.UTF8.GetBytes(cleanHeader)
            );
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool ok, string message, PaymentTransactionDto? transaction)> SimulateWebhookAsync(string orderId, string? utr = null, CancellationToken ct = default)
    {
        var tx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Include(t => t.Group)
            .FirstOrDefaultAsync(t => t.YoupiOrderId == orderId, ct);

        if (tx == null)
            return (false, $"Order {orderId} not found", null);

        var mockUtr = !string.IsNullOrWhiteSpace(utr)
            ? utr.Trim()
            : $"{DateTime.UtcNow.Ticks % 900000000000 + 100000000000}";

        var mockPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            orderId = tx.YoupiOrderId,
            status = "SUCCESS",
            amount = tx.Amount,
            utr = mockUtr,
            payerVpa = "payer@okaxis"
        });

        var (ok, msg) = await ProcessWebhookAsync(mockPayload, "", ct, bypassSignature: true);
        if (!ok) return (false, msg, null);

        var updatedTx = await _db.PaymentTransactions
            .Include(t => t.PayerUser)
            .Include(t => t.Group)
            .FirstOrDefaultAsync(t => t.YoupiOrderId == orderId, ct);

        return (true, "Simulated bank payment webhook processed successfully", MapToDto(updatedTx ?? tx));
    }
}
