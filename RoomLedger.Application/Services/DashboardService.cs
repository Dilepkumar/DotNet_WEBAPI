using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

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

    public async Task<DashboardDto> GetAsync(int groupId, string? month = null)
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

        var allExpenses = await _db.PoolExpenses
            .Include(e => e.Items)
                .ThenInclude(i => i.ExpenseCategory)
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .OrderByDescending(e => e.ExpenseDate)
            .ToListAsync();

        var spent = allExpenses.Sum(e => e.TotalAmount);
        var poolBalance = Math.Max(0, contributed - spent);

        var poolSpentThisMonth = allExpenses
            .Where(e => e.ExpenseDate.Year == now.Year && e.ExpenseDate.Month == now.Month)
            .Sum(e => e.TotalAmount);

        double poolRemainingPct = monthlyPoolTarget > 0
            ? Math.Max(0, Math.Min(100, Math.Round((double)(poolBalance / monthlyPoolTarget) * 100, 1))) : 100.0;

        // 3. Category & Item Breakdowns (Current Month vs All-Time vs Monthwise)
        var currentMonthExpenses = allExpenses
            .Where(e => e.ExpenseDate.Year == now.Year && e.ExpenseDate.Month == now.Month)
            .ToList();

        var currentCategories = ExtractCategories(currentMonthExpenses);
        var currentItems = ExtractItems(currentMonthExpenses, 15);

        var allTimeCategories = ExtractCategories(allExpenses);
        var allTimeItems = ExtractItems(allExpenses, 15);

        // Select the active breakdown based on optional query parameter
        List<DashboardCategoryDto> categories;
        List<DashboardItemBreakdownDto> itemBreakdown;

        if (!string.IsNullOrWhiteSpace(month))
        {
            if (month.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                categories = allTimeCategories;
                itemBreakdown = allTimeItems;
            }
            else
            {
                var specificMonthExpenses = allExpenses
                    .Where(e => e.ExpenseDate.ToString("yyyy-MM") == month)
                    .ToList();
                categories = ExtractCategories(specificMonthExpenses);
                itemBreakdown = ExtractItems(specificMonthExpenses, 15);
            }
        }
        else
        {
            categories = currentCategories;
            itemBreakdown = currentItems;
        }

        // 3c. Monthly Historical Categories & Items (all active months + last 6 months minimum)
        var monthKeysSet = new SortedSet<string>(allExpenses.Select(e => e.ExpenseDate.ToString("yyyy-MM")));
        for (int m = 5; m >= 0; m--)
        {
            monthKeysSet.Add(now.AddMonths(-m).ToString("yyyy-MM"));
        }

        var monthsList = monthKeysSet.Select(k =>
        {
            var parts = k.Split('-');
            var d = new DateTime(int.Parse(parts[0]), int.Parse(parts[1]), 1);
            return (Key: k, Label: d.ToString("MMM yyyy"));
        }).ToList();

        var monthlyCategories = new List<DashboardMonthlyCategoryDto>();
        var monthlyItems = new List<DashboardMonthlyItemDto>();

        foreach (var (mKey, mLabel) in monthsList)
        {
            var mExpenses = allExpenses.Where(e => e.ExpenseDate.ToString("yyyy-MM") == mKey).ToList();
            var mTotal = mExpenses.Sum(e => e.TotalAmount);

            var mCats = ExtractCategories(mExpenses).Select(c => new DashboardCategorySliceDto(
                c.CategoryName,
                c.Icon ?? PoolService.GetCategoryIcon(c.CategoryName),
                c.TotalAmount,
                c.Percentage,
                c.ItemCount
            )).ToList();

            monthlyCategories.Add(new DashboardMonthlyCategoryDto(
                mLabel,
                mKey,
                mTotal,
                mCats
            ));

            var mItms = ExtractItems(mExpenses, 12).Select(it => new DashboardItemSliceDto(
                it.ItemName,
                it.CategoryName,
                it.TotalAmount,
                1m,
                it.Count,
                it.Percentage
            )).ToList();

            monthlyItems.Add(new DashboardMonthlyItemDto(
                mLabel,
                mKey,
                mTotal,
                mItms
            ));
        }

        var maxMonthTotal = monthlyCategories.Any() ? monthlyCategories.Max(m => m.TotalAmount) : 0m;
        var monthlyTrends = monthlyCategories.TakeLast(6).Select(m => new MonthlyTrendDto(
            m.Month,
            m.TotalAmount,
            maxMonthTotal > 0 ? Math.Round((double)(m.TotalAmount / maxMonthTotal) * 100, 1) : 0
        )).ToList();

        var availableMonths = monthsList.Select(m => m.Key).OrderByDescending(k => k).ToList();

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
            var upiId = creditor?.UpiId;

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
            MonthlyItems: monthlyItems,
            AllTimeCategories: allTimeCategories,
            AllTimeItems: allTimeItems,
            AllTimePoolSpent: spent,
            AvailableMonths: availableMonths
        );
    }

    private static List<DashboardCategoryDto> ExtractCategories(IEnumerable<PoolExpense> expenses)
    {
        var categoryMap = new Dictionary<string, (int? CatId, string CatName, string Icon, decimal Total, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in expenses)
        {
            if (exp.Items != null && exp.Items.Count > 0)
            {
                foreach (var item in exp.Items)
                {
                    var catName = !string.IsNullOrWhiteSpace(item.ExpenseCategory?.Name)
                        ? item.ExpenseCategory.Name.Trim()
                        : (!string.IsNullOrWhiteSpace(exp.Category) ? exp.Category.Trim() : "Other");
                    var catIcon = !string.IsNullOrWhiteSpace(item.ExpenseCategory?.Icon)
                        ? item.ExpenseCategory.Icon
                        : PoolService.GetCategoryIcon(catName);
                    var catId = item.ExpenseCategoryId;

                    if (!categoryMap.TryGetValue(catName, out var cur))
                    {
                        categoryMap[catName] = (catId, catName, catIcon, item.Amount, 1);
                    }
                    else
                    {
                        categoryMap[catName] = (cur.CatId ?? catId, catName, cur.Icon, cur.Total + item.Amount, cur.Count + 1);
                    }
                }
            }
            else
            {
                var catName = !string.IsNullOrWhiteSpace(exp.Category) ? exp.Category.Trim() : "Other";
                var catIcon = PoolService.GetCategoryIcon(catName);

                if (!categoryMap.TryGetValue(catName, out var cur))
                {
                    categoryMap[catName] = (null, catName, catIcon, exp.TotalAmount, 1);
                }
                else
                {
                    categoryMap[catName] = (cur.CatId, catName, cur.Icon, cur.Total + exp.TotalAmount, cur.Count + 1);
                }
            }
        }

        var totalSpent = categoryMap.Values.Sum(c => c.Total);
        return categoryMap.Values
            .OrderByDescending(c => c.Total)
            .Select(c => new DashboardCategoryDto(
                c.CatId,
                c.CatName,
                c.Icon,
                c.Total,
                c.Count,
                totalSpent > 0 ? Math.Round((double)(c.Total / totalSpent) * 100, 1) : 0
            )).ToList();
    }

    private static List<DashboardItemBreakdownDto> ExtractItems(IEnumerable<PoolExpense> expenses, int take = 15)
    {
        var itemMap = new Dictionary<string, (string ItemName, string CatName, decimal Total, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in expenses)
        {
            if (exp.Items != null && exp.Items.Count > 0)
            {
                foreach (var item in exp.Items)
                {
                    var itemName = !string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemName.Trim() : exp.Description.Trim();
                    var catName = !string.IsNullOrWhiteSpace(item.ExpenseCategory?.Name)
                        ? item.ExpenseCategory.Name.Trim()
                        : (!string.IsNullOrWhiteSpace(exp.Category) ? exp.Category.Trim() : "Other");

                    if (!itemMap.TryGetValue(itemName, out var cur))
                    {
                        itemMap[itemName] = (itemName, catName, item.Amount, 1);
                    }
                    else
                    {
                        itemMap[itemName] = (itemName, cur.CatName, cur.Total + item.Amount, cur.Count + 1);
                    }
                }
            }
            else
            {
                var itemName = !string.IsNullOrWhiteSpace(exp.Description) ? exp.Description.Trim() : "General Expense";
                var catName = !string.IsNullOrWhiteSpace(exp.Category) ? exp.Category.Trim() : "Other";

                if (!itemMap.TryGetValue(itemName, out var cur))
                {
                    itemMap[itemName] = (itemName, catName, exp.TotalAmount, 1);
                }
                else
                {
                    itemMap[itemName] = (itemName, cur.CatName, cur.Total + exp.TotalAmount, cur.Count + 1);
                }
            }
        }

        var totalItemSpend = itemMap.Values.Sum(i => i.Total);
        return itemMap.Values
            .OrderByDescending(i => i.Total)
            .Take(take)
            .Select(it => new DashboardItemBreakdownDto(
                it.ItemName,
                it.CatName,
                it.Total,
                it.Count,
                totalItemSpend > 0 ? Math.Round((double)(it.Total / totalItemSpend) * 100, 1) : 0
            )).ToList();
    }
}
