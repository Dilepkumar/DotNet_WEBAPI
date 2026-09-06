using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class BillsService
{
    private readonly IApplicationDbContext _db;
    public BillsService(IApplicationDbContext db) => _db = db;

    // ───────────────────── CREATE BILL CONTAINER ─────────────────────
    public async Task<(bool ok, string message, object? result)> CreateAsync(int groupId, int userId, BillDto dto)
    {
        if (!await IsAdminAsync(groupId, userId))
            return (false, "Only group Admin can create bills", null);
        if (dto.Amount <= 0)
            return (false, "Amount must be greater than zero", null);
        if (dto.DueDayOfMonth is < 1 or > 28)
            return (false, "Due day must be between 1 and 28", null);

        var bill = new RecurringBill
        {
            GroupId = groupId,
            BillName = dto.BillName,
            Amount = dto.Amount,
            DueDayOfMonth = dto.DueDayOfMonth,
            NextBillingMonth = dto.BillingMonth
        };
        _db.RecurringBills.Add(bill);
        await _db.SaveChangesAsync();
        return (true, "Bill created", new { billId = bill.Id });
    }

    // ───────────── GENERATE SPLITS for a month (per-member checklist rows) ─────────────
    // Equal split across all active members. (Room-occupancy split comes with custom shares later.)
    public async Task<(bool ok, string message)> GenerateSplitsAsync(int groupId, int userId, string billingMonth)
    {
        if (!await IsAdminAsync(groupId, userId))
            return (false, "Only group Admin can generate bill splits");

        var memberIds = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == MemberStatus.Active)
            .Select(m => m.UserId).ToListAsync();

        var bills = await _db.RecurringBills
            .Where(b => b.GroupId == groupId && b.IsActive).ToListAsync();
        if (bills.Count == 0) return (false, "No active bills in this group");

        // prevent double-generation
        var existing = await _db.BillSplits
            .Where(s => s.GroupId == groupId && s.BillingMonth == billingMonth)
            .Select(s => (int?)s.RecurringBillId).Distinct().ToListAsync();

        foreach (var bill in bills)
        {
            if (existing.Contains(bill.Id)) continue;
            var perHead = Math.Round(bill.Amount / memberIds.Count, 2, MidpointRounding.AwayFromZero);
            foreach (var uid in memberIds)
                _db.BillSplits.Add(new BillSplit
                {
                    RecurringBillId = bill.Id,
                    GroupId = groupId,
                    BillingMonth = billingMonth,
                    UserId = uid,
                    ShareAmount = perHead
                });
        }
        await _db.SaveChangesAsync();
        return (true, $"Splits generated for {billingMonth}");
    }

    // ───────────── MONTH VIEW: checklist of who paid which bill ─────────────
    public async Task<object> GetMonthAsync(int groupId, string billingMonth)
    {
        var rows = await _db.BillSplits
            .Where(s => s.GroupId == groupId && s.BillingMonth == billingMonth)
            .Select(s => new
            {
                s.Id,
                s.RecurringBillId,
                BillName = s.RecurringBill!.BillName,
                TotalAmount = s.RecurringBill.Amount,
                DueDay = s.RecurringBill.DueDayOfMonth,
                s.UserId,
                UserName = s.User.FullName,
                s.ShareAmount,
                s.IsPaid,
                s.PaidAt
            })
            .ToListAsync();

        return new
        {
            month = billingMonth,
            bills = rows.GroupBy(r => new { r.RecurringBillId, r.BillName, r.TotalAmount, r.DueDay })
                        .Select(g => new
                        {
                            billId = g.Key.RecurringBillId,
                            billName = g.Key.BillName,
                            totalAmount = g.Key.TotalAmount,
                            dueDay = g.Key.DueDay,
                            perHead = g.First().ShareAmount, 
                            totalPaid = g.Count(x => x.IsPaid),
                            memberCount = g.Count(),
                            members = g.Select(x => new
                            { x.UserId, userName = x.UserName, x.ShareAmount, x.IsPaid, x.PaidAt })
                        })
        };
    }

    // ───────────── MARK MY SHARE PAID / UNPAID ─────────────
    public async Task<(bool ok, string message)> TogglePaidAsync(int groupId, int userId, int splitId)
    {
        var split = await _db.BillSplits.FirstOrDefaultAsync(s =>
            s.Id == splitId && s.GroupId == groupId && s.UserId == userId);
        if (split == null) return (false, "Split not found for this user");

        split.IsPaid = !split.IsPaid;
        split.PaidAt = split.IsPaid ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();
        return (true, split.IsPaid ? "Marked as paid" : "Marked as unpaid");
    }

    // ───────────── ADD / DEACTIVATE BILL (admin) ─────────────
    public async Task<(bool ok, string message)> DeactivateAsync(int groupId, int userId, int billId)
    {
        if (!await IsAdminAsync(groupId, userId))
            return (false, "Only group Admin can deactivate bills");
        var bill = await _db.RecurringBills.FirstOrDefaultAsync(b => b.Id == billId && b.GroupId == groupId);
        if (bill == null) return (false, "Bill not found");
        bill.IsActive = false;
        await _db.SaveChangesAsync();
        return (true, "Bill deactivated");
    }

    // ───────────── MONTH-OVER-MONTH TREND (analytics from your prompt) ─────────────
    public async Task<object> GetTrendAsync(int groupId)
    {
        var rows = await _db.BillSplits
            .Where(s => s.GroupId == groupId)
            .Select(s => new
            {
                s.BillingMonth,
                BillName = s.RecurringBill!.BillName,
                s.ShareAmount,
                s.IsPaid
            })
            .ToListAsync();

        var trend = rows
            .GroupBy(s => new { s.BillingMonth, s.BillName })
            .Select(g => new
            {
                month = g.Key.BillingMonth,
                bill = g.Key.BillName,
                billTotal = g.First().ShareAmount * g.Count(),
                paidCount = g.Count(x => x.IsPaid),
                memberCount = g.Count()
            })
            .ToList();

        return new
        {
            byMonthBill = trend.OrderBy(t => t.month),
            onTimeCompliance = trend.Count == 0 ? 0 :
                Math.Round(100m * trend.Sum(t => t.paidCount) / trend.Sum(t => t.memberCount), 1)
        };
    }

    private async Task<bool> IsAdminAsync(int groupId, int userId) =>
        await _db.GroupMembers.AnyAsync(m =>
            m.GroupId == groupId && m.UserId == userId &&
            m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);
}
