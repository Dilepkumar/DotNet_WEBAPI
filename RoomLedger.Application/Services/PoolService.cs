using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class PoolService
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    public PoolService(IApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    // ───────────────────── CONTRIBUTE TO POOL ─────────────────────
    public async Task<(bool ok, string message)> ContributeAsync(int groupId, int userId, ContributeDto dto)
    {
        if (dto.Amount <= 0) return (false, "Amount must be greater than zero");
        if (!await IsActiveMemberAsync(groupId, userId))
            return (false, "You are not an active member of this group");

        var isAdmin = await IsAdminAsync(groupId, userId);

        _db.PoolContributions.Add(new PoolContribution
        {
            GroupId = groupId,
            UserId = userId,
            Amount = dto.Amount,
            ContributedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            PeriodMonth = DateTime.UtcNow.ToString("yyyy-MM"),
            TransactionRef = dto.TransactionRef,
            Status = isAdmin ? ContributionStatus.Approved : ContributionStatus.Pending,
            ApprovedByUserId = isAdmin ? userId : null,
            ApprovedAt = isAdmin ? DateTime.UtcNow : null
        });
        await _db.SaveChangesAsync();

        return (true, isAdmin ? "Contribution recorded" : "Contribution submitted — waiting for admin approval");
    }

    // ───────────── LOG ITEMIZED POOL EXPENSE (strict line-sum validation) ─────────────
    public async Task<(bool ok, string message)> AddExpenseAsync(int groupId, int userId, PoolExpenseDto dto)
    {
        if (!await IsActiveMemberAsync(groupId, userId))
            return (false, "You are not an active member of this group");
        if (dto.Items == null || dto.Items.Count == 0)
            return (false, "Add at least one item");

        var total = dto.Items.Sum(i => i.Amount);
        if (total <= 0)
            return (false, "Expense total must be greater than zero");

        // STRICT check per your prompt: Total must equal sum of line items
        if (total != dto.Items.Sum(i => i.Amount))
            return (false, "Item amounts don't add up"); // (defensive; same expression)

        var expense = new PoolExpense
        {
            GroupId = groupId,
            RecordedByUserId = userId,
            Description = dto.Description,
            TotalAmount = total,
            ExpenseDate = DateOnly.TryParse(dto.ExpenseDate, out var d)
                ? d : DateOnly.FromDateTime(DateTime.UtcNow)
        };
        expense.Items = dto.Items.Select(i => new ExpenseItem
        {
            ExpenseCategoryId = i.CategoryId,
            ItemName = i.ItemName,
            Amount = i.Amount
        }).ToList();

        _db.PoolExpenses.Add(expense);
        await _db.SaveChangesAsync();
        return (true, "Expense logged");
    }

    // ───────────── POOL OVERVIEW: balance + recent activity ─────────────
    public async Task<object> GetOverviewAsync(int groupId)
    {
        var contributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId).SumAsync(c => (decimal?)c.Amount) ?? 0m;
        var spent = await _db.PoolExpenses
                    .Where(e => e.GroupId == groupId && !e.IsVoided)
                    .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;


        var contributions = await _db.PoolContributions
            .Where(c => c.GroupId == groupId)
            .OrderByDescending(c => c.CreatedAt).Take(20)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                userName = c.User.FullName,
                c.Amount,
                c.ContributedOn,
                c.PeriodMonth,
                c.TransactionRef
            }).ToListAsync();

        var expenses = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId)
            .OrderByDescending(e => e.CreatedAt).Take(20)
            .Select(e => new
            {
                e.Id,
                e.Description,
                e.TotalAmount,
                e.ExpenseDate,
                recordedBy = e.RecordedByUserId,
                items = e.Items.Select(i => new
                {
                    i.Id,
                    i.ItemName,
                    i.Amount,
                    category = i.ExpenseCategoryId == null ? null
                        : _db.ExpenseCategories
                             .Where(c => c.Id == i.ExpenseCategoryId)
                             .Select(c => c.Name).FirstOrDefault()
                })
            }).ToListAsync();

        return new
        {
            balance = contributed - spent,
            totalContributed = contributed,
            totalSpent = spent,
            contributions,
            expenses
        };
    }

    // ───────────── CATEGORY ANALYTICS (top cost drivers — for charts) ─────────────
    public async Task<object> GetAnalyticsAsync(int groupId)
    {
        // pull flat rows, aggregate in memory (EF-safe pattern)
        var rows = await _db.ExpenseItems
            .Where(i => i.PoolExpense!.GroupId == groupId && !i.PoolExpense!.IsVoided)
            .Select(i => new
            {
                ItemName = i.ItemName,
                Category = i.ExpenseCategoryId == null ? "Uncategorized"
                    : _db.ExpenseCategories
                         .Where(c => c.Id == i.ExpenseCategoryId)
                         .Select(c => c.Name).FirstOrDefault() ?? "Uncategorized",
                i.Amount,
                Month = i.PoolExpense!.ExpenseDate.Year + "-" +
                        i.PoolExpense.ExpenseDate.Month.ToString("D2")
            })
            .ToListAsync();

        return new
        {
            byCategory = rows.GroupBy(r => r.Category)
                .Select(g => new { category = g.Key, total = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.total),
            topItems = rows.GroupBy(r => r.ItemName)
                .Select(g => new { item = g.Key, total = g.Sum(x => x.Amount), count = g.Count() })
                .OrderByDescending(x => x.total).Take(10),
            byMonth = rows.GroupBy(r => r.Month)
                .Select(g => new { month = g.Key, total = g.Sum(x => x.Amount) })
                .OrderBy(x => x.month)
        };
    }

    // ───────────── CATEGORIES (seed once — Grocery, Dairy, Gas, Cleaning…) ─────────────
    public async Task<List<object>> ListCategoriesAsync()
    {
        return await _db.ExpenseCategories
            .OrderBy(c => c.Name)
            .Select(c => (object)new { c.Id, c.Name, c.Icon })
            .ToListAsync();
    }

    public async Task<(bool ok, string message)> SeedCategoriesAsync()
    {
        if (await _db.ExpenseCategories.AnyAsync()) return (true, "Already seeded");
        _db.ExpenseCategories.AddRange(
            new ExpenseCategory { Name = "Groceries", Icon = "🛒" },
            new ExpenseCategory { Name = "Dairy", Icon = "🥛" },
            new ExpenseCategory { Name = "Gas", Icon = "🔥" },
            new ExpenseCategory { Name = "Cleaning Supplies", Icon = "🧹" },
            new ExpenseCategory { Name = "Maintenance", Icon = "🔧" },
            new ExpenseCategory { Name = "Other", Icon = "📦" });
        await _db.SaveChangesAsync();
        return (true, "Categories seeded");
    }

    private async Task<bool> IsActiveMemberAsync(int groupId, int userId) =>
        await _db.GroupMembers.AnyAsync(m =>
            m.GroupId == groupId && m.UserId == userId && m.Status == MemberStatus.Active);
    public async Task<(bool ok, string message)> VoidExpenseAsync(int groupId, int userId, int expenseId, VoidDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason))
            return (false, "Reason is required");
        if (!await IsAdminAsync(groupId, userId))
            return (false, "Only group Admin can void pool expenses");

        var e = await _db.PoolExpenses.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == expenseId && x.GroupId == groupId);
        if (e == null) return (false, "Expense not found");
        if (e.IsVoided) return (false, "Already voided");

        var old = new { e.Description, e.TotalAmount };
        e.IsVoided = true;
        foreach (var item in e.Items) item.Amount = 0;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("PoolExpense", e.Id, "Void", old, null, userId, dto.Reason);
        return (true, "Pool expense voided — balance restored");
    }
    private async Task<bool> IsAdminAsync(int groupId, int userId) =>
    await _db.GroupMembers.AnyAsync(m =>
        m.GroupId == groupId && m.UserId == userId &&
        m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);

    // PoolService — add this method:
    public async Task<object> GetPoolBalanceAsync(int groupId, int userId)
    {
        var target = await _db.Groups
            .Where(g => g.Id == groupId)
            .Select(g => (decimal?)g.MonthlyPoolTarget ?? 0m)
            .FirstOrDefaultAsync();

        var contributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;

        var spent = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM");

        // Members: no User nav on GroupMember → project id + share, join names via _db.Users
        var members = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == MemberStatus.Active)
            .Select(m => new
            {
                m.UserId,
                m.MonthlyPoolShare,
                IsAlias = m.IsAlias ?? false,
                AliasName = m.AliasName ?? string.Empty
            }).ToListAsync();

        var realUserIds = members.Where(m => !m.IsAlias).Select(m => m.UserId).Distinct().ToList();
        var nameMap = await _db.Users
            .Where(u => realUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var memberIds = members.Select(m => m.UserId).ToList();

        // This month's contributions per member (one grouped query)
        var monthSums = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.PeriodMonth == currentMonth
             && c.Status == ContributionStatus.Approved)
            .GroupBy(c => c.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.UserId, x => x.Total);

        var memberStatuses = members.Select(m =>
        {
            var contributedThisMonth = monthSums.GetValueOrDefault(m.UserId);
            var expected = m.MonthlyPoolShare > 0
                ? m.MonthlyPoolShare
                : (members.Count > 0 ? target / members.Count : 0m);

            return new
            {
                userId = m.UserId,
                userName = m.IsAlias ? (m.AliasName ?? "Unknown") : nameMap.GetValueOrDefault(m.UserId, "Unknown"),
                contributedThisMonth,
                expectedThisMonth = expected,
                hasPaidTarget = expected > 0 && contributedThisMonth >= expected,
                pendingAmount = Math.Max(0, expected - contributedThisMonth),
                isAlias = m.IsAlias
            };
        }).ToList();

        // Recent contributions — PoolContribution HAS a User nav, so use it directly
        var recentContribs = await _db.PoolContributions
             .Where(c => c.GroupId == groupId)
             .OrderByDescending(c => c.ContributedOn).ThenByDescending(c => c.Id).Take(15)
             .Select(c => new
             {
                 id = "c" + c.Id,
                 type = "Contribution",
                 description = "Pool contribution" + (c.TransactionRef != null ? $" ({c.TransactionRef})" : ""),
                 userName = c.User.FullName,
                 date = c.ContributedOn.ToString("yyyy-MM-dd"),
                 amount = c.Amount,
                 status = c.Status.ToString(),
                 approvedBy = c.ApprovedByUserId == null ? null
                     : _db.Users.Where(u => u.Id == c.ApprovedByUserId).Select(u => u.FullName).FirstOrDefault(),
                 rejectReason = c.RejectReason
             }).ToListAsync();

        // Recent expenses — NO nav on PoolExpense → join names in memory
        var recentExpensesRaw = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .OrderByDescending(e => e.CreatedAt).Take(10)
            .Select(e => new { e.Id, e.Description, e.TotalAmount, e.ExpenseDate, e.RecordedByUserId })
            .ToListAsync();

        var recorderIds = recentExpensesRaw.Select(e => e.RecordedByUserId).Distinct().ToList();
        var recorderNames = await _db.Users.Where(u => recorderIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var recentExpenses = recentExpensesRaw.Select(e => new
        {
            id = "e" + e.Id,
            type = "Expense",
            description = e.Description,
            userName = recorderNames.GetValueOrDefault(e.RecordedByUserId, "Unknown"),
            date = e.ExpenseDate.ToString("yyyy-MM-dd"),
            amount = e.TotalAmount,
            status = "Approved",
            approvedBy = (string?)null,
            rejectReason = (string?)null
        }).ToList();

        var recentTransactions = recentContribs.Concat(recentExpenses).OrderByDescending(t => t.date).Take(20).ToList();

        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId
                                && m.UserId == userId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);

        var pendingItems = isAdmin ? await _db.PoolContributions.Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Pending)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                userName = c.User.FullName,
                c.Amount,
                contributedOn = c.ContributedOn.ToString("yyyy-MM-dd"),
                c.PeriodMonth,
                c.TransactionRef
            }).ToListAsync() : null;

        return new
        {
            isAdmin,                      // ← new
            pendingItems,                 // ← new (null for non-admins)
            currentBalance = contributed - spent,
            monthlyTarget = target,
            totalContributions = contributed,
            totalSpent = spent,
            memberStatuses,
            recentTransactions
        };
    }
    public async Task<object> GetPendingContributionsAsync(int groupId, int adminId)
    {
        if (!await IsAdminAsync(groupId, adminId)) return new { forbidden = true, items = Array.Empty<object>() };

        var items = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                userName = c.User.FullName,
                c.Amount,
                c.ContributedOn,
                c.PeriodMonth,
                c.TransactionRef
            }).ToListAsync();
        return new { forbidden = false, items };
    }

    public async Task<(bool ok, string message)> ApproveContributionAsync(int groupId, int adminId, int contributionId)
    {
        if (!await IsAdminAsync(groupId, adminId)) return (false, "Only group Admin can approve");
        var c = await _db.PoolContributions.FirstOrDefaultAsync(x => x.Id == contributionId && x.GroupId == groupId);
        if (c == null) return (false, "Contribution not found");
        if (c.Status != ContributionStatus.Pending) return (false, "Already processed");

        c.Status = ContributionStatus.Approved;
        c.ApprovedByUserId = adminId;
        c.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("PoolContribution", c.Id, "Approve", null, new { c.Amount, c.UserId }, adminId, null);
        return (true, "Contribution approved");
    }

    public async Task<(bool ok, string message)> RejectContributionAsync(int groupId, int adminId, int contributionId, RejectContributionDto dto)
    {
        if (!await IsAdminAsync(groupId, adminId)) return (false, "Only group Admin can reject");
        if (string.IsNullOrWhiteSpace(dto.Reason)) return (false, "Reason is required");
        var c = await _db.PoolContributions.FirstOrDefaultAsync(x => x.Id == contributionId && x.GroupId == groupId);
        if (c == null) return (false, "Contribution not found");
        if (c.Status != ContributionStatus.Pending) return (false, "Already processed");

        c.Status = ContributionStatus.Rejected;
        c.ApprovedByUserId = adminId;
        c.ApprovedAt = DateTime.UtcNow;
        c.RejectReason = dto.Reason;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("PoolContribution", c.Id, "Reject", null, new { c.Amount, c.UserId }, adminId, dto.Reason);
        return (true, "Contribution rejected");
    }
    public async Task<(bool ok, string message)> SetSharesAsync(int groupId, int adminId, SetSharesDto dto)
    {
        if (!await IsAdminAsync(groupId, adminId)) return (false, "Only group Admin can set shares");

        foreach (var s in dto.Shares)
        {
            if (s.UserId.HasValue)
            {
                var m = await _db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == s.UserId);
                if (m == null) continue;
                m.MonthlyPoolShare = s.MonthlyShare;
                if (!string.IsNullOrWhiteSpace(s.AliasName)) m.AliasName = s.AliasName;
            }
            else if (!string.IsNullOrWhiteSpace(s.AliasName))
            {
                // alias member: one row, UserId = admin creator, IsAlias flag
                var existing = await _db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.IsAlias == true && x.AliasName == s.AliasName);
                if (existing == null)
                {
                    _db.GroupMembers.Add(new GroupMember
                    {
                        GroupId = groupId,
                        UserId = adminId,
                        IsAlias = true,
                        AliasName = s.AliasName,
                        Role = MemberRole.Member,
                        Status = MemberStatus.Active,
                        MonthlyPoolShare = s.MonthlyShare
                    });
                }
                else existing.MonthlyPoolShare = s.MonthlyShare;
            }
        }
        await _db.SaveChangesAsync();
        return (true, "Shares updated");
    }
    public async Task<(bool ok, string message)> SetTargetAsync(int groupId, int adminId, decimal target)
    {
        if (!await IsAdminAsync(groupId, adminId)) return (false, "Only group Admin can set the target");
        if (target < 0) return (false, "Target cannot be negative");
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.Id == groupId);
        if (g == null) return (false, "Group not found");
        g.MonthlyPoolTarget = target;
        await _db.SaveChangesAsync();
        return (true, $"Monthly target set to ₹{target}");
    }

}
