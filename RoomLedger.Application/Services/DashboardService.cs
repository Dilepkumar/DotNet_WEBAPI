using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;

namespace RoomLedger.Application.Services;

public class DashboardService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _me;
    public DashboardService(IApplicationDbContext db, ICurrentUserService me)
    { _db = db; _me = me; }

    public async Task<DashboardDto> GetAsync(int groupId)
    {
        var uid = _me.UserId;

        // pool balance
        var contributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId).SumAsync(c => (decimal?)c.Amount) ?? 0m;
        var spent = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided).SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        // my unpaid bill shares this month
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var mySplits = await _db.BillSplits
            .Where(s => s.GroupId == groupId && s.BillingMonth == month && s.UserId == uid && !s.IsPaid)
            .ToListAsync();

        // outstanding IOU: reuse the simplified-debt rows the IouService computes
        var iou = await _db.IouParticipants
            .Where(p => p.IouExpense!.GroupId == groupId && p.UserId != p.IouExpense.PaidById)
            .ToListAsync();   // simplified: gross share owed; swap in IouService's net matrix later

        var iouDebt = iou.Sum(p => p.ShareAmount ?? 0m);

        return new DashboardDto(
            PoolBalance: contributed - spent,
            UnpaidBillTotal: mySplits.Sum(s => s.ShareAmount),
            OutstandingIouDebt: iouDebt,
            OutstandingIouCredit: 0m,   // wire from net matrix when you polish
            UnreadNotifications: await _db.Notifications
                .CountAsync(n => n.UserId == uid && !n.IsRead),
            UpcomingBills: mySplits.Select(s => new UpcomingBillDto(
                s.RecurringBill!.BillName, s.ShareAmount,
                s.RecurringBill.DueDayOfMonth, s.IsPaid)).ToList()
        );
    }
}
