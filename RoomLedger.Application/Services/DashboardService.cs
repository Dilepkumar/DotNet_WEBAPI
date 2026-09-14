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
            .Include(i => i.PoolExpense)
            .Where(i => i.PoolExpense != null && i.PoolExpense.GroupId == groupId && !i.PoolExpense.IsVoided)
            .ToListAsync();

        var categoryGroups = items
            .GroupBy(i =>
            {
                var catName = !string.IsNullOrWhiteSpace(i.ExpenseCategory?.Name)
                    ? i.ExpenseCategory.Name
                    : (!string.IsNullOrWhiteSpace(i.PoolExpense?.Category) ? i.PoolExpense.Category : "Other");
                var icon = !string.IsNullOrWhiteSpace(i.ExpenseCategory?.Icon)
                    ? i.ExpenseCategory.Icon
                    : PoolService.GetCategoryIcon(catName);
                return new
                {
                    CatId = i.ExpenseCategoryId,
                    CatName = catName,
                    Icon = icon
                };
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

        // 3b. Item Breakdown
        var itemGroups = items
            .GroupBy(i => new
            {
                ItemName = i.ItemName.Trim(),
                CatName = !string.IsNullOrWhiteSpace(i.ExpenseCategory?.Name)
                    ? i.ExpenseCategory.Name
                    : (!string.IsNullOrWhiteSpace(i.PoolExpense?.Category) ? i.PoolExpense.Category : "Other")
            })
            .Select(g => new
            {
                g.Key.ItemName,
                g.Key.CatName,
                Total = g.Sum(x => x.Amount),
                Count = g.Count()
            }).OrderByDescending(x => x.Total).Take(10).ToList();

        var totalItemSpend = itemGroups.Sum(x => x.Total);
        var itemBreakdown = itemGroups.Select(it => new DashboardItemBreakdownDto(
            it.ItemName,
            it.CatName,
            it.Total,
            it.Count,
            totalItemSpend > 0 ? Math.Round((double)(it.Total / totalItemSpend) * 100, 1) : 0
        )).ToList();

        // 3c. Monthly Historical Categories & Items (last 6 months)
        var sixMonthsAgo = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-5).Date);
        sixMonthsAgo = new DateOnly(sixMonthsAgo.Year, sixMonthsAgo.Month, 1);

        var historicalExpenses = await _db.PoolExpenses
            .Include(e => e.Items)
            .ThenInclude(i => i.ExpenseCategory)
            .Where(e => e.GroupId == groupId && !e.IsVoided && e.ExpenseDate >= sixMonthsAgo)
            .ToListAsync();

        var monthsList = new List<(string Key, string Label)>();
        var nowUtc = DateTime.UtcNow;
        for (int m = 5; m >= 0; m--)
        {
            var dt = nowUtc.AddMonths(-m);
            monthsList.Add((dt.ToString("yyyy-MM"), dt.ToString("MMM yyyy")));
        }

        var monthlyCategories = new List<DashboardMonthlyCategoryDto>();
        var monthlyItems = new List<DashboardMonthlyItemDto>();

        foreach (var (monthKey, monthLabel) in monthsList)
        {
            var monthExpenses = historicalExpenses.Where(e => e.ExpenseDate.ToString("yyyy-MM") == monthKey).ToList();
            var monthTotal = monthExpenses.Sum(e => e.TotalAmount);

            // Aggregate categories for this month
            var catMap = new Dictionary<string, (string Icon, decimal Total)>();
            foreach (var exp in monthExpenses)
            {
                if (exp.Items != null && exp.Items.Count > 0)
                {
                    foreach (var it in exp.Items)
                    {
                        var catName = !string.IsNullOrWhiteSpace(it.ExpenseCategory?.Name)
                            ? it.ExpenseCategory.Name
                            : (!string.IsNullOrWhiteSpace(exp.Category) ? exp.Category : "General");
                        var catIcon = !string.IsNullOrWhiteSpace(it.ExpenseCategory?.Icon)
                            ? it.ExpenseCategory.Icon
                            : "📦";
                        if (!catMap.ContainsKey(catName)) catMap[catName] = (catIcon, 0);
                        catMap[catName] = (catIcon, catMap[catName].Total + it.Amount);
                    }
                }
                else
                {
                    var catName = !string.IsNullOrWhiteSpace(exp.Category) ? exp.Category : "General";
                    if (!catMap.ContainsKey(catName)) catMap[catName] = ("📦", 0);
                    catMap[catName] = ("📦", catMap[catName].Total + exp.TotalAmount);
                }
            }

            var catSlices = catMap
                .Select(kv => new DashboardCategorySliceDto(
                    kv.Key,
                    kv.Value.Icon,
                    kv.Value.Total,
                    monthTotal > 0 ? Math.Round((double)(kv.Value.Total / monthTotal) * 100, 1) : 0
                ))
                .OrderByDescending(x => x.TotalAmount)
                .ToList();

            monthlyCategories.Add(new DashboardMonthlyCategoryDto(
                monthLabel,
                monthKey,
                monthTotal,
                catSlices
            ));

            // Aggregate items for this month
            var itmMap = new Dictionary<string, (string CatName, decimal Total, decimal Qty, int Count)>();
            foreach (var exp in monthExpenses)
            {
                if (exp.Items != null && exp.Items.Count > 0)
                {
                    foreach (var it in exp.Items)
                    {
                        var itName = it.ItemName.Trim();
                        var catName = !string.IsNullOrWhiteSpace(it.ExpenseCategory?.Name)
                            ? it.ExpenseCategory.Name
                            : (!string.IsNullOrWhiteSpace(exp.Category) ? exp.Category : "General");
                        if (!itmMap.ContainsKey(itName)) itmMap[itName] = (catName, 0, 0, 0);
                        var cur = itmMap[itName];
                        itmMap[itName] = (catName, cur.Total + it.Amount, cur.Qty + it.Quantity, cur.Count + 1);
                    }
                }
                else
                {
                    var itName = exp.Description.Trim();
                    var catName = !string.IsNullOrWhiteSpace(exp.Category) ? exp.Category : "General";
                    if (!itmMap.ContainsKey(itName)) itmMap[itName] = (catName, 0, 0, 0);
                    var cur = itmMap[itName];
                    itmMap[itName] = (catName, cur.Total + exp.TotalAmount, cur.Qty + 1, cur.Count + 1);
                }
            }

            var itmSlices = itmMap
                .Select(kv => new DashboardItemSliceDto(
                    kv.Key,
                    kv.Value.CatName,
                    kv.Value.Total,
                    kv.Value.Qty,
                    kv.Value.Count,
                    monthTotal > 0 ? Math.Round((double)(kv.Value.Total / monthTotal) * 100, 1) : 0
                ))
                .OrderByDescending(x => x.TotalAmount)
                .Take(12)
                .ToList();

            monthlyItems.Add(new DashboardMonthlyItemDto(
                monthLabel,
                monthKey,
                monthTotal,
                itmSlices
            ));
        }

        var maxMonthTotal = monthlyCategories.Any() ? monthlyCategories.Max(m => m.TotalAmount) : 0m;
        var monthlyTrends = monthlyCategories.Select(m => new MonthlyTrendDto(
            m.Month,
            m.TotalAmount,
            maxMonthTotal > 0 ? Math.Round((double)(m.TotalAmount / maxMonthTotal) * 100, 1) : 0
        )).ToList();

        // 4. Recent Pool Expenses with full timestamp, item details, and recorder name
        var recentExpensesEntities = await _db.PoolExpenses
            .Include(e => e.Items)
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .OrderByDescending(e => e.Id)
            .Take(5)
            .ToListAsync();

        var involvedUserIds = recentExpensesEntities
            .Select(e => e.RecordedByUserId)
            .Concat(recentExpensesEntities.Where(e => e.PaidByUserId.HasValue).Select(e => e.PaidByUserId!.Value))
            .Distinct()
            .ToList();

        var userNameMap = await _db.Users
            .Where(u => involvedUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var recentExpenses = recentExpensesEntities.Select(e =>
        {
            var recorder = userNameMap.GetValueOrDefault(e.RecordedByUserId, "Roommate");
            var payer = e.PaidByUserId.HasValue
                ? userNameMap.GetValueOrDefault(e.PaidByUserId.Value, e.PayerName ?? "Roommate")
                : "Central Pool";

            var userDisplay = e.PaidByUserId.HasValue
                ? $"{payer} (Paid own money · {(e.IsReimbursed ? "Reimbursed from Pool ✓" : "Pending Reimbursement")})"
                : $"Central Pool (Added by {recorder})";

            return new DashboardExpenseDto(
                e.Id,
                e.Description,
                e.TotalAmount,
                e.CreatedAt.ToString("o"),
                userDisplay,
                e.PaidByUserId != null ? "me" : "pool",
                e.ReceiptUrl,
                e.Items.Select(i => new DashboardExpenseItemDto(i.ItemName, i.Amount, i.Quantity)).ToList(),
                recorder,
                e.IsReimbursed
            );
        }).ToList();

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
            ItemBreakdown: itemBreakdown,
            MonthlyTrends: monthlyTrends,
            RecentExpenses: recentExpenses,
            IouDebts: iouDebts,
            MonthlyCategories: monthlyCategories,
            MonthlyItems: monthlyItems
        );
    }
}
