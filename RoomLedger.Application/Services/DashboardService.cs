using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;

namespace RoomLedger.Application.Services;

public class DashboardService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _me;
    private readonly IouService _iou;

    public DashboardService(IApplicationDbContext db, ICurrentUserService me, IouService iou)
    {
        _db = db;
        _me = me;
        _iou = iou;
    }

    public async Task<DashboardDto> GetAsync(int groupId)
    {
        var uid = _me.UserId;
        var now = DateTime.UtcNow;
        var currentMonth = now.ToString("yyyy-MM");

        // 1. Group Identity
        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
        var groupName = group?.GroupName ?? "My Flat";
        var groupAddress = group?.Address ?? $"{groupName} Flat";
        var memberCount = await _db.GroupMembers.CountAsync(m => m.GroupId == groupId && m.Status == MemberStatus.Active);
        var monthlyPoolTarget = group?.MonthlyPoolTarget ?? 0m;

        // 2. Pool Fund KPI & Spend
        var contributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;

        var spent = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        var poolBalance = Math.Max(0, contributed - spent);

        var poolSpentThisMonth = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && e.ExpenseDate.Year == now.Year && e.ExpenseDate.Month == now.Month)
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        double poolRemainingPct = monthlyPoolTarget > 0
            ? Math.Max(0, Math.Min(100, Math.Round((double)(poolBalance / monthlyPoolTarget) * 100, 1))) : 100.0;

        // 3. Category Expense Breakdown
        var items = await _db.ExpenseItems
            .Include(i => i.ExpenseCategory)
            .Where(i => i.PoolExpense != null && i.PoolExpense.GroupId == groupId && !i.PoolExpense.IsVoided)
            .ToListAsync();

        var categoryGroups = items
            .GroupBy(i => new
            {
                CatId = i.ExpenseCategoryId,
                CatName = !string.IsNullOrWhiteSpace(i.ExpenseCategory?.Name) ? i.ExpenseCategory.Name : "General",
                Icon = !string.IsNullOrWhiteSpace(i.ExpenseCategory?.Icon) ? i.ExpenseCategory.Icon : "📦"
            })
            .Select(g => new
            {
                g.Key.CatId,
                g.Key.CatName,
                g.Key.Icon,
                Total = g.Sum(x => x.Amount),
                Count = g.Count()
            }).OrderByDescending(x => x.Total).ToList();

        var totalSpentAll = categoryGroups.Sum(x => x.Total);
        var categories = categoryGroups.Select(c => new DashboardCategoryDto(
            c.CatId,
            c.CatName,
            c.Icon,
            c.Total,
            c.Count,
            totalSpentAll > 0 ? Math.Round((double)(c.Total / totalSpentAll) * 100, 1) : 0
        )).ToList();

        // 4. Recent Pool Expenses with item details
        var recentExpensesEntities = await _db.PoolExpenses
            .Include(e => e.Items)
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .OrderByDescending(e => e.Id)
            .Take(5)
            .ToListAsync();

        var recentExpenses = recentExpensesEntities.Select(e => new DashboardExpenseDto(
            e.Id,
            e.Description,
            e.TotalAmount,
            e.ExpenseDate.ToString("yyyy-MM-dd"),
            e.PayerName ?? "Pool Fund",
            e.PaidByUserId != null ? "me" : "pool",
            e.ReceiptUrl,
            e.Items.Select(i => new DashboardExpenseItemDto(i.ItemName, i.Amount, i.Quantity)).ToList()
        )).ToList();

        // 5. Fixed Recurring Bills
        var allSplits = await _db.BillSplits
            .Include(s => s.RecurringBill)
            .Where(s => s.GroupId == groupId && s.BillingMonth == currentMonth)
            .ToListAsync();

        var pendingBillsCount = allSplits.Where(s => !s.IsPaid).Select(s => s.RecurringBillId).Distinct().Count();
        var myUnpaidTotal = allSplits.Where(s => s.UserId == uid && !s.IsPaid).Sum(s => s.ShareAmount);

        var upcomingBills = allSplits
            .Where(s => s.UserId == uid)
            .Select(s => new UpcomingBillDto(
                s.RecurringBill?.BillName ?? "Recurring Bill",
                s.ShareAmount,
                s.RecurringBill?.DueDayOfMonth ?? 1,
                s.IsPaid
            )).ToList();

        // 6. Net IOU & Debts
        var simplifiedDebts = await _iou.GetSimplifiedDebtsAsync(groupId);
        var debtUserIds = simplifiedDebts.Select(d => d.FromUserId).Concat(simplifiedDebts.Select(d => d.ToUserId)).Distinct().ToList();
        var usersMap = await _db.Users
            .Where(u => debtUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u);

        var iouDebts = simplifiedDebts.Select(d =>
        {
            var debtor = usersMap.GetValueOrDefault(d.FromUserId);
            var creditor = usersMap.GetValueOrDefault(d.ToUserId);
            var creditorEmail = creditor?.Email ?? "";
            var userPrefix = !string.IsNullOrEmpty(creditorEmail) ? creditorEmail.Split('@')[0] : "user";
            var upiId = $"{userPrefix}@okhdfcbank";

            return new DashboardIouDebtDto(
                d.FromUserId,
                debtor?.FullName ?? $"User #{d.FromUserId}",
                d.ToUserId,
                creditor?.FullName ?? $"User #{d.ToUserId}",
                d.Amount,
                upiId
            );
        }).ToList();

        var myOwedToMe = simplifiedDebts.Where(d => d.ToUserId == uid).Sum(d => d.Amount);
        var myIOwe = simplifiedDebts.Where(d => d.FromUserId == uid).Sum(d => d.Amount);

        // 7. Unread Notifications Count
        var unreadNotifications = await _db.Notifications.CountAsync(n => n.UserId == uid && !n.IsRead);

        return new DashboardDto(
            GroupName: groupName,
            GroupAddress: groupAddress,
            MemberCount: memberCount,
            MonthlyPoolTarget: monthlyPoolTarget,
            PoolBalance: poolBalance,
            PoolSpentThisMonth: poolSpentThisMonth,
            PoolRemainingPercentage: poolRemainingPct,
            PendingBillsCount: pendingBillsCount,
            UnpaidBillTotal: myUnpaidTotal,
            OutstandingIouDebt: myIOwe,
            OutstandingIouCredit: myOwedToMe,
            UnreadNotifications: unreadNotifications,
            UpcomingBills: upcomingBills,
            CategoryBreakdown: categories,
            RecentExpenses: recentExpenses,
            IouDebts: iouDebts
        );
    }
}
