using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.Common.Models;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class ElectricityBillService
{
    private readonly IApplicationDbContext _db;
    private readonly IElectricityBillProvider _electricityBillProvider;
    private readonly NotificationService _notifications;
    private readonly ILogger<ElectricityBillService> _logger;

    public ElectricityBillService(IApplicationDbContext db, IElectricityBillProvider electricityBillProvider, NotificationService notifications, ILogger<ElectricityBillService> logger)
    {
        _db = db;
        _electricityBillProvider = electricityBillProvider;
        _notifications = notifications;
        _logger = logger;
    }

    // ───────────────────── BILLERS METADATA ─────────────────────

    public Task<List<ElectricityBillerInfo>> GetBillersAsync(CancellationToken ct = default)
    {
        var billers = new List<ElectricityBillerInfo>
        {
            new("TGSPDCL00TEL01", "Southern Power Distribution Company of Telangana Ltd (TGSPDCL)", "Telangana", "Hyderabad & Southern Telangana")
        };
        return Task.FromResult(billers);
    }

    public Task<ElectricityBillerDetailDto?> GetBillerDetailsAsync(string billerId, CancellationToken ct = default)
    {
        var detail = new ElectricityBillerDetailDto(
            "TGSPDCL00TEL01",
            "Southern Power Distribution Company of Telangana Ltd (TGSPDCL)",
            "ELECTRICITY",
            "Telangana",
            new List<ElectricityCustomerParam>
            {
                new("UniqueServiceNumber", "Unique Service Number (USCNO)", "NUMERIC", false, 7, 10, @"^\d{7,10}$", "Enter 9 or 10-digit USCNO from your TGSPDCL bill"),
                new("ServiceNumber", "Service Number (Optional)", "ALPHANUMERIC", true, 4, 15, null, "e.g. V6047888"),
            });
        return Task.FromResult<ElectricityBillerDetailDto?>(detail);
    }

    /// <summary>
    /// Direct TGSPDCL bill enquiry for instant testing and verification via API / Swagger
    /// </summary>
    public async Task<NormalizedElectricityBillResult> FetchBillDirectAsync(string uscno, CancellationToken ct = default)
    {
        var dummy = new ElectricityAccount
        {
            BillerId = "TGSPDCL00TEL01",
            ConsumerNumber = uscno.Trim(),
            CustomerParametersJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["UniqueServiceNumber"] = uscno.Trim()
            })
        };
        return await _electricityBillProvider.FetchBillAsync(dummy, ct);
    }

    // ───────────────────── ACCOUNT MANAGEMENT ─────────────────────

    public async Task<(bool ok, string message, ElectricityAccountDto? account)> CreateAccountAsync(int userId, CreateElectricityAccountDto dto, CancellationToken ct = default)
    {
        // 1. Verify user belongs to the group
        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == dto.GroupId && m.UserId == userId && m.Status == MemberStatus.Active, ct);
        if (!isMember)
            return (false, "You must be an active member of the group to add an electricity account", null);

        var billerName = "Southern Power Distribution Company of Telangana Ltd (TGSPDCL)";

        // 2. Resolve Consumer Number / Unique Service Number
        var consumerNumber = dto.ConsumerNumber?.Trim();
        if (string.IsNullOrWhiteSpace(consumerNumber))
        {
            foreach (var kvp in dto.CustomerParameters)
            {
                var key = kvp.Key.ToLower();
                if (key.Contains("consumer") || key.Contains("uscno") || key.Contains("ukscno") || key.Contains("service") || key.Contains("ca") || key.Contains("account"))
                {
                    consumerNumber = kvp.Value.Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(consumerNumber) && dto.CustomerParameters.Count > 0)
        {
            consumerNumber = dto.CustomerParameters.Values.First().Trim();
        }

        if (string.IsNullOrWhiteSpace(consumerNumber))
            return (false, "Unique Service Number (USCNO) or Consumer Number is required", null);

        // Check if account already exists in this group
        var exists = await _db.ElectricityAccounts.AnyAsync(
            a => a.GroupId == dto.GroupId && a.BillerId == dto.BillerId && a.ConsumerNumber == consumerNumber && a.IsActive, ct);
        if (exists)
            return (false, $"An active electricity account for {consumerNumber} already exists in this group", null);

        var jsonParams = JsonSerializer.Serialize(dto.CustomerParameters);

        // Calculate initial check schedule
        var now = IndianTime.Now;
        DateTime? nextCheck = now; // Ready to check right away
        DateTime? estimatedNextBillDate = null;

        if (dto.ExpectedBillDayOfMonth.HasValue && dto.ExpectedBillDayOfMonth.Value >= 1 && dto.ExpectedBillDayOfMonth.Value <= 31)
        {
            var day = dto.ExpectedBillDayOfMonth.Value;
            var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
            var safeDay = Math.Min(day, daysInMonth);
            var expectedThisMonth = new DateTime(now.Year, now.Month, safeDay);

            if (now.Date <= expectedThisMonth.Date)
            {
                estimatedNextBillDate = expectedThisMonth;
                var startMonitor = expectedThisMonth.AddDays(-3).Date.AddHours(10);
                nextCheck = now >= startMonitor ? now : startMonitor;
            }
            else
            {
                var nextMonth = now.AddMonths(1);
                var safeNextDay = Math.Min(day, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
                estimatedNextBillDate = new DateTime(nextMonth.Year, nextMonth.Month, safeNextDay);
                var startMonitor = estimatedNextBillDate.Value.AddDays(-3).Date.AddHours(10);
                nextCheck = startMonitor;
            }
        }

        var account = new ElectricityAccount
        {
            GroupId = dto.GroupId,
            BillerId = !string.IsNullOrWhiteSpace(dto.BillerId) ? dto.BillerId : "TGSPDCL00TEL01",
            BillerName = billerName,
            ConsumerNumber = consumerNumber,
            CustomerParametersJson = jsonParams,
            CustomerName = null,
            CreatedByUserId = userId,
            IsActive = true,
            ExpectedBillDayOfMonth = dto.ExpectedBillDayOfMonth,
            EstimatedNextBillDate = estimatedNextBillDate,
            MonitoringStatus = "MONITORING",
            NextCheckAt = nextCheck,
            ManualChecksTodayCount = 0,
            LastManualCheckDate = null,
            CreatedAt = now
        };

        _db.ElectricityAccounts.Add(account);
        await _db.SaveChangesAsync(ct);

        // Attempt initial check immediately
        try
        {
            await ProcessBillFetchInternalAsync(account, isManual: false, userId: userId, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial check for new account {AccountId} failed gracefully", account.Id);
        }

        var resultDto = await MapAccountToDtoAsync(account, ct);
        return (true, "Electricity account connected successfully", resultDto);
    }

    public async Task<List<ElectricityAccountDto>> GetAccountsByGroupAsync(int groupId, CancellationToken ct = default)
    {
        var accounts = await _db.ElectricityAccounts
            .Where(a => a.GroupId == groupId && a.IsActive)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        var list = new List<ElectricityAccountDto>();
        foreach (var acc in accounts)
        {
            list.Add(await MapAccountToDtoAsync(acc, ct));
        }
        return list;
    }

    public async Task<ElectricityAccountDto?> GetAccountByIdAsync(int accountId, CancellationToken ct = default)
    {
        var account = await _db.ElectricityAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account == null) return null;
        return await MapAccountToDtoAsync(account, ct);
    }

    public async Task<(bool ok, string message)> DeleteAccountAsync(int accountId, int userId, CancellationToken ct = default)
    {
        var account = await _db.ElectricityAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null)
            return (false, "Electricity account not found");

        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == account.GroupId && m.UserId == userId && m.Status == MemberStatus.Active, ct);
        if (!isMember)
            return (false, "Unauthorized: you must be a member of this group");

        account.IsActive = false;
        account.MonitoringStatus = "INACTIVE";
        account.NextCheckAt = null;
        await _db.SaveChangesAsync(ct);

        return (true, "Electricity account deleted successfully");
    }

    public async Task<(bool ok, string message)> UpdateAccountAsync(int accountId, int userId, UpdateElectricityAccountDto dto, CancellationToken ct = default)
    {
        var account = await _db.ElectricityAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null)
            return (false, "Electricity account not found");

        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == account.GroupId && m.UserId == userId && m.Status == MemberStatus.Active, ct);
        if (!isMember)
            return (false, "Unauthorized: you must be a member of this group");

        if (dto.ExpectedBillDayOfMonth.HasValue)
        {
            account.ExpectedBillDayOfMonth = dto.ExpectedBillDayOfMonth.Value;
        }

        if (dto.IsActive.HasValue)
        {
            account.IsActive = dto.IsActive.Value;
        }

        await _db.SaveChangesAsync(ct);
        return (true, "Account updated successfully");
    }

    // ───────────────────── MANUAL CHECK (MAX 2 PER CALENDAR DAY) ─────────────────────

    public async Task<(bool ok, string message, ElectricityAccountDto? account, ElectricityBillDto? bill)> CheckBillManualAsync(
        int accountId,
        int userId,
        CancellationToken ct = default)
    {
        var account = await _db.ElectricityAccounts
            .Include(a => a.Bills)
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account == null)
            return (false, "Electricity account not found", null, null);

        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == account.GroupId && m.UserId == userId && m.Status == MemberStatus.Active, ct);
        if (!isMember)
            return (false, "Unauthorized: you must be a member of this group", null, null);

        // Enforce maximum 2 manual checks per calendar day (IST)
        ResetDailyManualCheckIfNewDay(account);

        if (account.ManualChecksTodayCount >= 2)
        {
            var accountDtoLimit = await MapAccountToDtoAsync(account, ct);
            return (false, "Daily manual bill-check limit reached. Next check available tomorrow.", accountDtoLimit, accountDtoLimit.LatestBill);
        }

        // Increment manual check count
        account.ManualChecksTodayCount++;
        account.LastManualCheckDate = IndianTime.Today;

        // Perform bill fetch
        var (ok, statusMsg, billEntity, _) = await ProcessBillFetchInternalAsync(account, isManual: true, userId: userId, ct: ct);

        var accountDto = await MapAccountToDtoAsync(account, ct);
        var billDto = billEntity != null ? MapBillToDto(billEntity) : accountDto.LatestBill;

        return (ok, statusMsg, accountDto, billDto);
    }

    // ───────────────────── LATEST BILL & MONITORING & SPLITS ─────────────────────

    public async Task<ElectricityBillDto?> GetLatestBillAsync(int accountId, CancellationToken ct = default)
    {
        var bill = await _db.ElectricityBills
            .Where(b => b.ElectricityAccountId == accountId)
            .OrderByDescending(b => b.BillDate ?? b.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (bill == null) return null;

        // Auto-heal splits if missing (only if not deactivated by admin)
        if (!bill.IsSplitCreated || bill.RecurringBillId == null)
        {
            var isDeactivated = bill.RecurringBillId.HasValue &&
                await _db.RecurringBills.AnyAsync(rb => rb.Id == bill.RecurringBillId.Value && !rb.IsActive, ct);

            if (!isDeactivated)
            {
                var account = await _db.ElectricityAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account != null)
                {
                    await CreateGroupBillSplitAsync(account, bill, ct);
                }
            }
        }

        return MapBillToDto(bill);
    }

    public async Task<List<ElectricityBillDto>> GetBillHistoryAsync(int accountId, CancellationToken ct = default)
    {
        var bills = await _db.ElectricityBills
            .Where(b => b.ElectricityAccountId == accountId)
            .OrderByDescending(b => b.BillDate ?? b.CreatedAt)
            .ToListAsync(ct);

        return bills.Select(MapBillToDto).ToList();
    }

    public async Task<ElectricityMonitoringStatusDto?> GetMonitoringStatusAsync(int accountId, CancellationToken ct = default)
    {
        var account = await _db.ElectricityAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null) return null;

        ResetDailyManualCheckIfNewDay(account);

        var logs = await _db.ElectricityCheckLogs
            .Where(l => l.ElectricityAccountId == accountId)
            .OrderByDescending(l => l.CheckedAt)
            .Take(10)
            .Select(l => new ElectricityCheckLogDto(
                l.Id,
                l.Id,
                l.CheckType,
                l.Status,
                l.Message,
                l.CheckedAt))
            .ToListAsync(ct);

        var remainingChecks = Math.Max(0, 2 - account.ManualChecksTodayCount);

        return new ElectricityMonitoringStatusDto(
            account.Id,
            account.Id,
            account.BillerName,
            account.ConsumerNumber,
            account.MonitoringStatus,
            account.LastCheckedAt,
            account.NextCheckAt,
            account.LastCheckStatus,
            account.LastCheckMessage,
            account.ManualChecksTodayCount,
            remainingChecks,
            remainingChecks > 0,
            logs);
    }

    public async Task<ElectricityBillSplitsDto?> GetBillSplitsAsync(int billId, CancellationToken ct = default)
    {
        var bill = await _db.ElectricityBills.FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill == null) return null;

        var account = await _db.ElectricityAccounts.FirstOrDefaultAsync(a => a.Id == bill.ElectricityAccountId, ct);

        // Auto-heal splits if not created yet or RecurringBillId is missing (only if not deactivated by admin)
        if (account != null && (!bill.IsSplitCreated || bill.RecurringBillId == null))
        {
            var isDeactivated = bill.RecurringBillId.HasValue &&
                await _db.RecurringBills.AnyAsync(rb => rb.Id == bill.RecurringBillId.Value && !rb.IsActive, ct);

            if (!isDeactivated)
            {
                await CreateGroupBillSplitAsync(account, bill, ct);
            }
        }

        var splits = new List<ElectricitySplitItemDto>();

        if (bill.RecurringBillId.HasValue)
        {
            splits = await (
                from bs in _db.BillSplits
                join u in _db.Users on bs.UserId equals u.Id
                where bs.RecurringBillId == bill.RecurringBillId.Value
                select new ElectricitySplitItemDto(
                    u.Id,
                    u.FullName,
                    bs.ShareAmount,
                    bs.IsPaid,
                    bs.PaidAt)
            ).ToListAsync(ct);
        }

        // Resilient fallback preview if splits table had no rows
        if (splits.Count == 0 && account != null)
        {
            var activeMembers = await (
                from gm in _db.GroupMembers
                join u in _db.Users on gm.UserId equals u.Id
                where gm.GroupId == account.GroupId && gm.Status == MemberStatus.Active
                select new { u.Id, u.FullName }
            ).ToListAsync(ct);

            if (activeMembers.Count > 0)
            {
                var equalShare = Math.Round(bill.TotalAmount / activeMembers.Count, 2, MidpointRounding.AwayFromZero);
                decimal runningTotal = 0;
                for (int i = 0; i < activeMembers.Count; i++)
                {
                    var m = activeMembers[i];
                    decimal share = (i == activeMembers.Count - 1) ? (bill.TotalAmount - runningTotal) : equalShare;
                    runningTotal += share;
                    splits.Add(new ElectricitySplitItemDto(m.Id, m.FullName, share, false, null));
                }
            }
        }

        return new ElectricityBillSplitsDto(
            bill.Id,
            bill.BillNumber,
            bill.BillPeriod,
            bill.TotalAmount,
            bill.DueDate,
            splits);
    }

    public async Task<ElectricityBillSplitsDto?> GetSplitsAsync(int accountId, int? billId = null, CancellationToken ct = default)
    {
        if (billId.HasValue && billId.Value > 0)
        {
            return await GetBillSplitsAsync(billId.Value, ct);
        }

        var latestBill = await _db.ElectricityBills
            .Where(b => b.ElectricityAccountId == accountId)
            .OrderByDescending(b => b.BillDate ?? b.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (latestBill == null) return null;

        return await GetBillSplitsAsync(latestBill.Id, ct);
    }

    // ───────────────────── BACKGROUND JOB EXECUTION ─────────────────────

    public async Task ProcessDueAccountsForMonitoringJobAsync(CancellationToken ct = default)
    {
        var now = IndianTime.Now;

        var dueAccounts = await _db.ElectricityAccounts
            .Where(a => a.IsActive &&
                        a.NextCheckAt != null &&
                        a.NextCheckAt <= now &&
                        (a.MonitoringStatus == "MONITORING" || a.MonitoringStatus == "BILL_GENERATED"))
            .ToListAsync(ct);

        _logger.LogInformation("ElectricityBillMonitoringJob: Found {Count} due accounts to process at {Time}", dueAccounts.Count, now);

        foreach (var account in dueAccounts)
        {
            try
            {
                if (account.MonitoringStatus == "BILL_GENERATED")
                {
                    account.MonitoringStatus = "MONITORING";
                }

                await ProcessBillFetchInternalAsync(account, isManual: false, userId: null, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing electricity account {AccountId} in monitoring job", account.Id);
            }
        }
    }

    // ───────────────────── CORE FETCH & CYCLE LOGIC ─────────────────────

    private async Task<(bool ok, string message, ElectricityBill? bill, string status)> ProcessBillFetchInternalAsync(ElectricityAccount account, bool isManual, int? userId, CancellationToken ct)
    {
        var now = IndianTime.Now;

        // 1. Call Electricity Bill Provider
        NormalizedElectricityBillResult fetchResult;
        try
        {
            fetchResult = await _electricityBillProvider.FetchBillAsync(account, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception calling electricity provider for account {AccountId}", account.Id);
            fetchResult = new NormalizedElectricityBillResult
            {
                Status = "PROVIDER_ERROR",
                ErrorMessage = $"Provider call failed: {ex.Message}"
            };
        }

        // 2. Update account tracking
        account.LastCheckedAt = now;
        account.LastCheckStatus = fetchResult.Status;
        account.LastCheckMessage = fetchResult.ErrorMessage ?? $"Status: {fetchResult.Status}";

        // 3. Log check
        var checkLog = new ElectricityCheckLog
        {
            ElectricityAccountId = account.Id,
            CheckedByUserId = userId,
            CheckType = isManual ? "MANUAL" : "AUTOMATIC",
            Status = fetchResult.Status,
            Message = fetchResult.ErrorMessage ?? (fetchResult.Status == "BILL_GENERATED"
                ? $"Bill detected: ₹{fetchResult.TotalAmount} (Due: {fetchResult.DueDate:dd MMM yyyy})"
                : fetchResult.Status),
            CheckedAt = now,
            RawResponse = fetchResult.RawResponse
        };
        _db.ElectricityCheckLogs.Add(checkLog);

        ElectricityBill? savedBill = null;

        // 4. Handle Statuses
        switch (fetchResult.Status)
        {
            case "BILL_GENERATED":
            {
                if (!string.IsNullOrWhiteSpace(fetchResult.CustomerName))
                {
                    account.CustomerName = fetchResult.CustomerName;
                }

                // Check for duplicate bill
                var isDuplicate = await IsDuplicateBillAsync(account, fetchResult, ct);

                if (isDuplicate)
                {
                    _logger.LogInformation("Bill for account {AccountId} already exists in DB. Checking if splits exist...", account.Id);

                    savedBill = await _db.ElectricityBills
                        .Where(b => b.ElectricityAccountId == account.Id)
                        .OrderByDescending(b => b.BillDate ?? b.CreatedAt)
                        .FirstOrDefaultAsync(ct);

                    account.MonitoringStatus = "BILL_GENERATED";
                    account.EstimatedNextBillDate = CalculateNextEstimatedBillDate(account, savedBill);
                    account.NextCheckAt = CalculateNextCycleMonitoringStart(account.EstimatedNextBillDate);

                    if (savedBill != null && (!savedBill.IsSplitCreated || savedBill.RecurringBillId == null))
                    {
                        await CreateGroupBillSplitAsync(account, savedBill, ct);
                    }
                }
                else
                {
                    // Create NEW bill
                    savedBill = new ElectricityBill
                    {
                        ElectricityAccountId = account.Id,
                        GroupId = account.GroupId,
                        BillerId = !string.IsNullOrWhiteSpace(fetchResult.BillerId) ? fetchResult.BillerId : account.BillerId,
                        BillerName = !string.IsNullOrWhiteSpace(fetchResult.BillerName) ? fetchResult.BillerName : account.BillerName,
                        ConsumerNumber = !string.IsNullOrWhiteSpace(fetchResult.ConsumerNumber) ? fetchResult.ConsumerNumber : account.ConsumerNumber,
                        CustomerName = fetchResult.CustomerName ?? account.CustomerName,
                        BillNumber = fetchResult.BillNumber,
                        BillDate = fetchResult.BillDate,
                        BillPeriod = fetchResult.BillPeriod,
                        DueDate = fetchResult.DueDate,
                        BillAmount = fetchResult.BillAmount,
                        ACDAmount = fetchResult.ACDAmount,
                        Arrears = fetchResult.Arrears,
                        LateFee = fetchResult.LateFee,
                        TotalAmount = fetchResult.TotalAmount,
                        ProviderReference = fetchResult.ProviderReference,
                        RawProviderResponse = fetchResult.RawResponse,
                        FetchSource = isManual ? "MANUAL" : "BACKGROUND_JOB",
                        CreatedAt = now
                    };

                    _db.ElectricityBills.Add(savedBill);
                    await _db.SaveChangesAsync(ct);

                    // Stop monitoring current billing cycle
                    account.MonitoringStatus = "BILL_GENERATED";

                    // Schedule next billing cycle check (approx 2-3 days before expected bill generation)
                    account.EstimatedNextBillDate = CalculateNextEstimatedBillDate(account, savedBill);
                    account.NextCheckAt = CalculateNextCycleMonitoringStart(account.EstimatedNextBillDate);

                    // Create Group Equal Split
                    await CreateGroupBillSplitAsync(account, savedBill, ct);
                }

                break;
            }

            case "NO_BILL":
            {
                // Check if an existing bill was previously recorded and has now been settled at TGSPDCL (e.g. via PhonePe)
                var latestBill = await _db.ElectricityBills
                    .Where(b => b.ElectricityAccountId == account.Id)
                    .OrderByDescending(b => b.BillDate ?? b.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                if (latestBill != null)
                {
                    savedBill = latestBill;

                    if (!latestBill.IsPaidAtProvider)
                    {
                        latestBill.IsPaidAtProvider = true;
                        latestBill.PaidAtProviderDate = now;
                        account.MonitoringStatus = "BILL_SETTLED";
                        account.LastCheckMessage = "Bill settled at TGSPDCL (₹0.00 outstanding). Paid via PhonePe / external platform.";
                        _logger.LogInformation("Electricity bill {BillId} for account {AccountId} settled at TGSPDCL (paid externally).", latestBill.Id, account.Id);
                    }

                    // Since bill is settled, schedule check for next month's billing cycle
                    account.EstimatedNextBillDate = CalculateNextEstimatedBillDate(account, latestBill);
                    account.NextCheckAt = CalculateNextCycleMonitoringStart(account.EstimatedNextBillDate);
                }
                else
                {
                    account.MonitoringStatus = "MONITORING";
                    account.NextCheckAt = GetNextScheduledCheckSlot(now);
                }

                break;
            }

            case "PROVIDER_ERROR":
            {
                account.MonitoringStatus = "MONITORING";
                account.NextCheckAt = now.AddHours(2);
                break;
            }

            case "INVALID_CONSUMER":
            {
                account.MonitoringStatus = "INVALID_CONSUMER";
                account.NextCheckAt = null;
                break;
            }

            default:
            {
                account.MonitoringStatus = "MONITORING";
                account.NextCheckAt = GetNextScheduledCheckSlot(now);
                break;
            }
        }

        await _db.SaveChangesAsync(ct);

        var msg = fetchResult.Status switch
        {
            "BILL_GENERATED" => $"Bill generated: ₹{fetchResult.TotalAmount:N2} due on {fetchResult.DueDate:dd MMM yyyy}",
            "NO_BILL" => (savedBill != null && savedBill.IsPaidAtProvider)
                ? "Bill is paid & settled at TGSPDCL (₹0.00 pending dues). Settled via PhonePe / external platform."
                : "No active bill is pending. Automatic monitoring is active.",
            "INVALID_CONSUMER" => fetchResult.ErrorMessage ?? "Invalid electricity service number.",
            "PROVIDER_ERROR" => fetchResult.ErrorMessage ?? "TGSPDCL bill service is temporarily unavailable.",
            _ => "Check completed."
        };

        var isSuccess = fetchResult.Status != "PROVIDER_ERROR" && fetchResult.Status != "INVALID_CONSUMER";

        return (isSuccess, msg, savedBill, fetchResult.Status);
    }

    // ───────────────────── DUPLICATE BILL CHECK ─────────────────────

    private async Task<bool> IsDuplicateBillAsync(ElectricityAccount account, NormalizedElectricityBillResult fetchResult, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(fetchResult.BillNumber))
        {
            var exists = await _db.ElectricityBills.AnyAsync(b =>
                b.ElectricityAccountId == account.Id &&
                b.BillNumber == fetchResult.BillNumber, ct);
            if (exists) return true;
        }

        if (fetchResult.BillDate.HasValue)
        {
            var exists = await _db.ElectricityBills.AnyAsync(b =>
                b.ElectricityAccountId == account.Id &&
                b.BillDate == fetchResult.BillDate.Value, ct);
            if (exists) return true;
        }

        if (!string.IsNullOrWhiteSpace(fetchResult.BillPeriod))
        {
            var exists = await _db.ElectricityBills.AnyAsync(b =>
                b.ElectricityAccountId == account.Id &&
                b.BillPeriod == fetchResult.BillPeriod, ct);
            if (exists) return true;
        }

        return false;
    }

    // ───────────────────── EQUAL GROUP SPLITTING ─────────────────────

    private async Task CreateGroupBillSplitAsync(ElectricityAccount account, ElectricityBill bill, CancellationToken ct)
    {
        if (bill.TotalAmount <= 0) return;

        var activeMembers = await _db.GroupMembers
            .Where(m => m.GroupId == account.GroupId && m.Status == MemberStatus.Active)
            .ToListAsync(ct);

        if (activeMembers.Count == 0) return;

        // Standardize billingMonth to yyyy-MM (e.g. 2026-10) matching BillsService and Dashboard
        string billingMonth;
        if (bill.BillDate.HasValue)
        {
            billingMonth = bill.BillDate.Value.ToString("yyyy-MM");
        }
        else if (!string.IsNullOrWhiteSpace(bill.BillPeriod) &&
                 DateTime.TryParseExact(bill.BillPeriod.Trim(), new[] { "MMM yyyy", "MMMM yyyy", "yyyy-MM", "MM/yyyy" },
                     System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedMonth))
        {
            billingMonth = parsedMonth.ToString("yyyy-MM");
        }
        else if (bill.DueDate.HasValue)
        {
            billingMonth = bill.DueDate.Value.ToString("yyyy-MM");
        }
        else
        {
            billingMonth = IndianTime.Now.ToString("yyyy-MM");
        }

        var dueDay = bill.DueDate?.Day ?? 15;
        var shortBiller = (bill.BillerName ?? account.BillerName ?? "").Contains("TGSPDCL", StringComparison.OrdinalIgnoreCase)
            ? "TGSPDCL"
            : (!string.IsNullOrWhiteSpace(bill.BillerName) && bill.BillerName.Length > 15 ? bill.BillerName.Substring(0, 15) : bill.BillerName ?? "Utility");
        var billName = $"Electricity ({shortBiller})";

        // Find or create RecurringBill
        RecurringBill? recurringBill = null;
        if (bill.RecurringBillId.HasValue)
        {
            recurringBill = await _db.RecurringBills.FirstOrDefaultAsync(rb => rb.Id == bill.RecurringBillId.Value, ct);
        }

        if (recurringBill == null)
        {
            recurringBill = await _db.RecurringBills
                .FirstOrDefaultAsync(rb => rb.GroupId == account.GroupId &&
                                           (rb.BillName == billName || rb.BillName.Contains("TGSPDCL") || rb.BillName.Contains("Southern Power")), ct);
        }

        if (recurringBill == null)
        {
            recurringBill = new RecurringBill
            {
                GroupId = account.GroupId,
                BillName = billName,
                Amount = bill.TotalAmount,
                DueDayOfMonth = dueDay,
                IsActive = true,
                NextBillingMonth = DateOnly.FromDateTime(bill.DueDate ?? IndianTime.Now.AddMonths(1)),
                PaidFromPool = false
            };
            _db.RecurringBills.Add(recurringBill);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            recurringBill.IsActive = true;
            recurringBill.BillName = billName;
            recurringBill.Amount = bill.TotalAmount;
            recurringBill.DueDayOfMonth = dueDay;
            recurringBill.NextBillingMonth = DateOnly.FromDateTime(bill.DueDate ?? IndianTime.Now.AddMonths(1));
            await _db.SaveChangesAsync(ct);
        }

        // Generate BillSplits if not yet created for this recurring bill and billing month
        var existingSplits = await _db.BillSplits
            .Where(s => s.RecurringBillId == recurringBill.Id && s.BillingMonth == billingMonth)
            .ToListAsync(ct);

        var equalShare = Math.Round(bill.TotalAmount / activeMembers.Count, 2, MidpointRounding.AwayFromZero);

        if (existingSplits.Count == 0)
        {
            decimal runningTotal = 0;
            for (int i = 0; i < activeMembers.Count; i++)
            {
                var member = activeMembers[i];
                decimal memberShare = (i == activeMembers.Count - 1)
                    ? (bill.TotalAmount - runningTotal)
                    : equalShare;

                runningTotal += memberShare;

                var split = new BillSplit
                {
                    RecurringBillId = recurringBill.Id,
                    GroupId = account.GroupId,
                    BillingMonth = billingMonth,
                    UserId = member.UserId,
                    ShareAmount = memberShare,
                    IsPaid = false,
                    PaidAt = null
                };
                _db.BillSplits.Add(split);
            }
        }
        else
        {
            // Ensure amounts match exactly
            decimal runningTotal = 0;
            for (int i = 0; i < existingSplits.Count; i++)
            {
                var s = existingSplits[i];
                decimal memberShare = (i == existingSplits.Count - 1)
                    ? (bill.TotalAmount - runningTotal)
                    : equalShare;
                runningTotal += memberShare;
                s.ShareAmount = memberShare;
            }
        }

        bill.IsSplitCreated = true;
        bill.RecurringBillId = recurringBill.Id;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created/healed equal split for bill {BillId}: ₹{Amount} among {Count} members (RecurringBill #{RecurId}).",
            bill.Id, bill.TotalAmount, activeMembers.Count, recurringBill.Id);

        foreach (var member in activeMembers)
        {
            try
            {
                await _notifications.PushAsync(
                    member.UserId,
                    account.GroupId,
                    "⚡ New Electricity Bill Detected",
                    $"Electricity bill of ₹{bill.TotalAmount:N2} has arrived. Your share: ₹{equalShare:N2}. Due on {bill.DueDate:dd MMM}.",
                    "BILL");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification to user {UserId} for electricity bill split", member.UserId);
            }
        }
    }

    // ───────────────────── BILLING CYCLE ESTIMATION ─────────────────────

    private static DateTime CalculateNextEstimatedBillDate(ElectricityAccount account, ElectricityBill? currentBill)
    {
        var billDate = currentBill?.BillDate ?? IndianTime.Now;

        if (account.ExpectedBillDayOfMonth.HasValue)
        {
            var day = account.ExpectedBillDayOfMonth.Value;
            var nextMonth = billDate.AddMonths(1);
            var safeDay = Math.Min(day, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
            return new DateTime(nextMonth.Year, nextMonth.Month, safeDay);
        }

        var nextEstimatedMonth = billDate.AddMonths(1);
        var targetDay = Math.Min(billDate.Day, DateTime.DaysInMonth(nextEstimatedMonth.Year, nextEstimatedMonth.Month));
        return new DateTime(nextEstimatedMonth.Year, nextEstimatedMonth.Month, targetDay);
    }

    private static DateTime CalculateNextCycleMonitoringStart(DateTime? estimatedNextBillDate)
    {
        if (estimatedNextBillDate == null)
        {
            return IndianTime.Now.AddDays(25).Date.AddHours(10);
        }

        var start = estimatedNextBillDate.Value.AddDays(-3).Date.AddHours(10);
        return start > IndianTime.Now ? start : IndianTime.Now.AddHours(12);
    }

    private static DateTime GetNextScheduledCheckSlot(DateTime fromTime)
    {
        var today10Am = fromTime.Date.AddHours(10);
        var today10Pm = fromTime.Date.AddHours(22);
        var tomorrow10Am = fromTime.Date.AddDays(1).AddHours(10);

        if (fromTime < today10Am) return today10Am;
        if (fromTime < today10Pm) return today10Pm;
        return tomorrow10Am;
    }

    private static void ResetDailyManualCheckIfNewDay(ElectricityAccount account)
    {
        var today = IndianTime.Today;
        if (account.LastManualCheckDate == null || account.LastManualCheckDate < today)
        {
            account.ManualChecksTodayCount = 0;
            account.LastManualCheckDate = today;
        }
    }

    private async Task<ElectricityAccountDto> MapAccountToDtoAsync(ElectricityAccount account, CancellationToken ct)
    {
        ResetDailyManualCheckIfNewDay(account);

        Dictionary<string, string> parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(account.CustomerParametersJson) ?? new();
        }
        catch
        {
            parameters = new();
        }

        var latestBill = await _db.ElectricityBills
            .Where(b => b.ElectricityAccountId == account.Id)
            .OrderByDescending(b => b.BillDate ?? b.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var latestBillDto = latestBill != null ? MapBillToDto(latestBill) : null;
        var remainingChecks = Math.Max(0, 2 - account.ManualChecksTodayCount);

        return new ElectricityAccountDto(
            account.Id,
            account.Id,
            account.GroupId,
            account.BillerId,
            account.BillerName,
            account.ConsumerNumber,
            parameters,
            account.CustomerName,
            account.CreatedByUserId,
            account.IsActive,
            account.ExpectedBillDayOfMonth,
            account.EstimatedNextBillDate,
            account.MonitoringStatus,
            account.LastCheckedAt,
            account.NextCheckAt,
            account.LastCheckStatus,
            account.LastCheckMessage,
            account.ManualChecksTodayCount,
            remainingChecks,
            account.CreatedAt,
            latestBillDto);
    }

    private static ElectricityBillDto MapBillToDto(ElectricityBill bill)
    {
        return new ElectricityBillDto(
            bill.Id,
            bill.Id,
            bill.ElectricityAccountId,
            bill.GroupId,
            bill.BillerId,
            bill.BillerName,
            bill.ConsumerNumber,
            bill.CustomerName,
            bill.BillNumber,
            bill.BillDate,
            bill.BillPeriod,
            bill.DueDate,
            bill.BillAmount,
            bill.ACDAmount,
            bill.Arrears,
            bill.LateFee,
            bill.TotalAmount,
            bill.ProviderReference,
            bill.FetchSource,
            bill.IsSplitCreated,
            bill.CreatedAt,
            bill.IsPaidAtProvider,
            bill.PaidAtProviderDate);
    }
}
