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
        if (dto.Amount <= 0)
            return (false, "Amount must be greater than zero");
        if (!await IsActiveMemberAsync(groupId, userId))
            return (false, "You are not an active member of this group");

        _db.PoolContributions.Add(new PoolContribution
        {
            GroupId = groupId,
            UserId = userId,
            Amount = dto.Amount,
            ContributedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            PeriodMonth = DateTime.UtcNow.ToString("yyyy-MM"),
            TransactionRef = dto.TransactionRef
        });
        await _db.SaveChangesAsync();
        return (true, "Contribution recorded");
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
        m.GroupId == groupId && m.UserId == userId && m.Status == MemberStatus.Active);
}
