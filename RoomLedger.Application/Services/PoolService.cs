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
        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var note = !string.IsNullOrWhiteSpace(dto.Message) ? dto.Message.Trim() : dto.TransactionRef?.Trim();

        // If multiple members specified (e.g. ₹500 × 5 members or ₹8,000 / 5 members)
        if (dto.MemberUserIds != null && dto.MemberUserIds.Count > 0)
        {
            var memberIds = dto.MemberUserIds.Distinct().ToList();
            var count = memberIds.Count;

            decimal perPersonAmount = (dto.Mode == "total_split" && count > 0)
                ? Math.Round(dto.Amount / count, 2)
                : dto.Amount;

            decimal totalAdded = perPersonAmount * count;

            foreach (var mId in memberIds)
            {
                _db.PoolContributions.Add(new PoolContribution
                {
                    GroupId = groupId,
                    UserId = mId,
                    Amount = perPersonAmount,
                    ContributedOn = today,
                    PeriodMonth = currentMonth,
                    TransactionRef = note,
                    Message = note,
                    Status = isAdmin ? ContributionStatus.Approved : ContributionStatus.Pending,
                    ApprovedByUserId = isAdmin ? userId : null,
                    ApprovedAt = isAdmin ? DateTime.UtcNow : null
                });
            }

            await _db.SaveChangesAsync();
            return (true, isAdmin
                ? $"Added ₹{totalAdded:N0} to Pool ({count} members × ₹{perPersonAmount:N0})"
                : $"Submitted contribution for {count} members — waiting for admin approval");
        }
        else
        {
            _db.PoolContributions.Add(new PoolContribution
            {
                GroupId = groupId,
                UserId = userId,
                Amount = dto.Amount,
                ContributedOn = today,
                PeriodMonth = currentMonth,
                TransactionRef = note,
                Message = note,
                Status = isAdmin ? ContributionStatus.Approved : ContributionStatus.Pending,
                ApprovedByUserId = isAdmin ? userId : null,
                ApprovedAt = isAdmin ? DateTime.UtcNow : null
            });

            await _db.SaveChangesAsync();
            return (true, isAdmin ? $"Added ₹{dto.Amount:N0} to Pool" : "Contribution submitted — waiting for admin approval");
        }
    }

    // ───────────── LOG ITEMIZED POOL EXPENSE (strict line-sum validation) ─────────────
    public async Task<(bool ok, string message)> AddExpenseAsync(int groupId, int userId, PoolExpenseDto dto)
    {
        if (!await IsActiveMemberAsync(groupId, userId))
            return (false, "You are not an active member of this group");
        if (string.IsNullOrWhiteSpace(dto.Description))
            return (false, "Please provide an expense description");

        var items = dto.Items != null && dto.Items.Count > 0
            ? dto.Items : new List<PoolItemDto>();

        var total = items.Sum(i => i.Amount);
        if (total <= 0)
            return (false, "Expense total must be greater than zero");

        int? paidByUserId = null;
        string payerName = "Central Pool";
        if (dto.PayerType == "me" || dto.PaidByUserId.HasValue)
        {
            paidByUserId = dto.PaidByUserId ?? userId;
            var payer = await _db.Users.FindAsync(paidByUserId.Value);
            payerName = payer?.FullName ?? "Roommate";
        }

        var expense = new PoolExpense
        {
            GroupId = groupId,
            RecordedByUserId = userId,
            PaidByUserId = paidByUserId,
            PayerName = payerName,
            Description = dto.Description.Trim(),
            TotalAmount = total,
            ExpenseDate = DateOnly.TryParse(dto.ExpenseDate, out var d)
                ? d : DateOnly.FromDateTime(DateTime.UtcNow),
            ReceiptUrl = dto.ReceiptUrl,
            Category = dto.Category,
            IsReimbursed = !paidByUserId.HasValue
        };

        var rawCategory = !string.IsNullOrWhiteSpace(dto.Category) ? dto.Category.Trim() : "Groceries";
        var cleanCat = System.Text.RegularExpressions.Regex.Replace(rawCategory, @"^[\p{Cs}\p{So}\p{Sk}\p{Cn}\s]+", "").Trim();
        if (string.IsNullOrWhiteSpace(cleanCat)) cleanCat = rawCategory;

        var matchedCat = await _db.ExpenseCategories
            .FirstOrDefaultAsync(c => c.Name.ToLower() == cleanCat.ToLower() ||
                                      c.Name.ToLower() == rawCategory.ToLower() ||
                                      cleanCat.ToLower().Contains(c.Name.ToLower()) ||
                                      c.Name.ToLower().Contains(cleanCat.ToLower()));
        if (matchedCat == null)
        {
            matchedCat = new ExpenseCategory
            {
                Name = cleanCat,
                Icon = GetCategoryIcon(cleanCat)
            };
            _db.ExpenseCategories.Add(matchedCat);
            await _db.SaveChangesAsync();
        }

        expense.Category = matchedCat.Name;
        var categoryId = matchedCat.Id;

        expense.Items = items.Select(i => new ExpenseItem
        {
            ExpenseCategoryId = i.CategoryId ?? categoryId,
            ItemName = string.IsNullOrWhiteSpace(i.ItemName) ? dto.Description.Trim() : i.ItemName.Trim(),
            Quantity = i.Quantity ?? 1m,
            Amount = i.Amount
        }).ToList();


        _db.PoolExpenses.Add(expense);
        await _db.SaveChangesAsync();

        if (dto.RecordInBills)
        {
            var expDate = expense.ExpenseDate;
            var monthStr = expDate.ToString("yyyy-MM");
            var dueDay = Math.Clamp(expDate.Day, 1, 28);

            var recurringBill = new RecurringBill
            {
                GroupId = groupId,
                BillName = dto.Description.Trim(),
                Amount = total,
                DueDayOfMonth = dueDay,
                NextBillingMonth = expDate,
                PaidFromPool = true
            };
            _db.RecurringBills.Add(recurringBill);
            await _db.SaveChangesAsync();

            var activeMembers = await _db.GroupMembers
                .Where(m => m.GroupId == groupId && m.Status == MemberStatus.Active)
                .Select(m => m.UserId).ToListAsync();

            if (activeMembers.Count > 0)
            {
                var perHead = Math.Round(total / activeMembers.Count, 2, MidpointRounding.AwayFromZero);
                foreach (var uid in activeMembers)
                {
                    _db.BillSplits.Add(new BillSplit
                    {
                        RecurringBillId = recurringBill.Id,
                        GroupId = groupId,
                        BillingMonth = monthStr,
                        UserId = uid,
                        ShareAmount = perHead,
                        IsPaid = true,
                        PaidAt = DateTime.UtcNow
                    });
                }
                await _db.SaveChangesAsync();
            }
        }

        var successMsg = dto.RecordInBills
            ? $"Expense of ₹{total} logged from Central Pool & recorded in Fixed Recurring Bills!"
            : (paidByUserId.HasValue
                ? $"Expense logged: Reimbursed ₹{total} to {payerName} from pool fund"
                : $"Expense of ₹{total} logged from Central Pool");

        return (true, successMsg);
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

    public static string GetCategoryIcon(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "📦";
        var lower = category.ToLower();
        if (lower.Contains("groc")) return "🥕";
        if (lower.Contains("dair") || lower.Contains("milk")) return "🥛";
        if (lower.Contains("util") || lower.Contains("bill") || lower.Contains("power")) return "⚡";
        if (lower.Contains("clean") || lower.Contains("house")) return "🧴";
        if (lower.Contains("food") || lower.Contains("snack")) return "🍕";
        if (lower.Contains("rent")) return "🏠";
        if (lower.Contains("maint") || lower.Contains("repair")) return "🔧";
        if (lower.Contains("travel") || lower.Contains("cab") || lower.Contains("trans")) return "🚕";
        return "📦";
    }

    public async Task<(bool ok, string message)> SeedCategoriesAsync()
    {
        if (await _db.ExpenseCategories.AnyAsync()) return (true, "Already seeded");
        _db.ExpenseCategories.AddRange(
            new ExpenseCategory { Name = "Groceries", Icon = "🥕" },
            new ExpenseCategory { Name = "Dairy & Essentials", Icon = "🥛" },
            new ExpenseCategory { Name = "Utilities & Bills", Icon = "⚡" },
            new ExpenseCategory { Name = "Cleaning & Household", Icon = "🧴" },
            new ExpenseCategory { Name = "Food & Snacks", Icon = "🍕" },
            new ExpenseCategory { Name = "Maintenance & Repairs", Icon = "🔧" },
            new ExpenseCategory { Name = "Travel & Transport", Icon = "🚕" },
            new ExpenseCategory { Name = "Rent", Icon = "🏠" },
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
            m.GroupId == groupId && m.UserId == userId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);


    // PoolService — GetPoolBalanceAsync:
    public async Task<object> GetPoolBalanceAsync(int groupId, int userId)
    {
        var target = await _db.Groups
            .Where(g => g.Id == groupId)
            .Select(g => (decimal?)g.MonthlyPoolTarget ?? 0m)
            .FirstOrDefaultAsync();

        var contributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;

        // Money actually withdrawn from pool: direct pool payments + reimbursed out-of-pocket expenses
        var spent = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed))
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        var pendingReimbursementsTotal = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && e.PaidByUserId.HasValue && !e.IsReimbursed)
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
                 description = !string.IsNullOrWhiteSpace(c.Message)
                     ? c.Message
                     : ("Pool contribution" + (c.TransactionRef != null ? $" ({c.TransactionRef})" : "")),
                 userName = c.User.FullName,
                 date = c.CreatedAt.ToString("o"),
                 amount = c.Amount,
                 status = c.Status.ToString(),
                 approvedBy = c.ApprovedByUserId == null ? null
                     : _db.Users.Where(u => u.Id == c.ApprovedByUserId).Select(u => u.FullName).FirstOrDefault(),
                 rejectReason = c.RejectReason
             }).ToListAsync();

        // Recent expenses — include itemized receipt items & payer/receipt metadata
        var recentExpensesRaw = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .OrderByDescending(e => e.CreatedAt).Take(15)
            .Select(e => new
            {
                e.Id,
                e.Description,
                e.TotalAmount,
                e.ExpenseDate,
                e.CreatedAt,
                e.RecordedByUserId,
                e.PaidByUserId,
                e.PayerName,
                e.ReceiptUrl,
                e.Category,
                e.IsReimbursed,
                Items = e.Items.Select(i => new { i.Id, i.ItemName, i.Quantity, i.Amount, i.ExpenseCategoryId }).ToList()
            })
            .ToListAsync();

        var involvedUserIds = recentExpensesRaw
            .Select(e => e.RecordedByUserId)
            .Concat(recentExpensesRaw.Where(e => e.PaidByUserId.HasValue).Select(e => e.PaidByUserId!.Value))
            .Distinct()
            .ToList();

        var userNameMap = await _db.Users.Where(u => involvedUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var recentExpenses = recentExpensesRaw.Select(e =>
        {
            var recorder = userNameMap.GetValueOrDefault(e.RecordedByUserId, "Roommate");
            var payer = e.PaidByUserId.HasValue
                ? userNameMap.GetValueOrDefault(e.PaidByUserId.Value, e.PayerName ?? "Roommate")
                : "Central Pool";

            var userDisplay = e.PaidByUserId.HasValue
                ? $"{payer} (Paid own money · {(e.IsReimbursed ? "Reimbursed from Pool ✓" : "Pending Reimbursement")})"
                : $"Central Pool (Added by {recorder})";

            return new
            {
                id = "e" + e.Id,
                type = "Expense",
                description = e.Description,
                userName = userDisplay,
                date = e.CreatedAt.ToString("o"),
                amount = e.TotalAmount,
                status = e.PaidByUserId.HasValue ? (e.IsReimbursed ? "Reimbursed ✓" : "Pending Reimbursement") : "Approved",
                approvedBy = (string?)null,
                rejectReason = (string?)null,
                payerType = e.PaidByUserId.HasValue ? "member" : "pool",
                payerName = payer,
                recorderName = recorder,
                receiptUrl = e.ReceiptUrl,
                category = e.Category,
                isReimbursed = e.IsReimbursed,
                items = e.Items
            };
        }).ToList();

        var recentTransactions = recentContribs.Select(c => new
        {
            c.id,
            c.type,
            c.description,
            c.userName,
            c.date,
            c.amount,
            c.status,
            c.approvedBy,
            c.rejectReason,
            payerType = "member",
            payerName = c.userName,
            recorderName = c.userName,
            receiptUrl = (string?)null,
            category = (string?)null,
            isReimbursed = true,
            items = (object?)null
        })
        .Concat(recentExpenses.Select(e => new
        {
            e.id,
            e.type,
            e.description,
            e.userName,
            e.date,
            e.amount,
            e.status,
            e.approvedBy,
            e.rejectReason,
            e.payerType,
            e.payerName,
            e.recorderName,
            e.receiptUrl,
            e.category,
            e.isReimbursed,
            items = (object?)e.items
        }))
        .OrderByDescending(t => t.date).Take(20).ToList();

        // Dynamic Category Breakdown & Out-of-Pocket Reimbursements
        var allExpenses = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .Select(e => new
            {
                e.Id,
                e.TotalAmount,
                e.Category,
                e.PaidByUserId,
                e.PayerName,
                e.IsReimbursed,
                Items = e.Items.Select(i => new
                {
                    i.Id,
                    i.ItemName,
                    i.Amount,
                    i.ExpenseCategoryId
                }).ToList()
            })
            .ToListAsync();

        var totalExpenseAmount = allExpenses.Sum(e => e.TotalAmount);
        var categoryBreakdown = allExpenses
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? "Other" : e.Category.Trim())
            .Select(g =>
            {
                var catTotal = g.Sum(x => x.TotalAmount);
                var catItems = g.SelectMany(x => x.Items)
                    .GroupBy(i => string.IsNullOrWhiteSpace(i.ItemName) ? "Item" : i.ItemName.Trim())
                    .Select(ig => new
                    {
                        itemName = ig.Key,
                        total = ig.Sum(i => i.Amount),
                        count = ig.Count()
                    })
                    .OrderByDescending(i => i.total)
                    .ToList();

                return new
                {
                    category = g.Key,
                    total = catTotal,
                    percentage = totalExpenseAmount > 0 ? Math.Round((catTotal / totalExpenseAmount) * 100, 1) : 0,
                    itemCount = catItems.Count,
                    items = catItems
                };
            })
            .OrderByDescending(x => x.total)
            .ToList();

        // Overall item-wise tracking across all pool expenses
        var itemBreakdown = allExpenses
            .SelectMany(e => e.Items.Select(i => new
            {
                ItemName = string.IsNullOrWhiteSpace(i.ItemName) ? (e.Category ?? "General") : i.ItemName.Trim(),
                i.Amount,
                Category = string.IsNullOrWhiteSpace(e.Category) ? "Other" : e.Category.Trim()
            }))
            .GroupBy(i => i.ItemName)
            .Select(g => new
            {
                itemName = g.Key,
                category = g.First().Category,
                total = g.Sum(i => i.Amount),
                count = g.Count()
            })
            .OrderByDescending(i => i.total)
            .ToList();

        // Out-of-Pocket Reimbursement Summary
        var outOfPocketSummary = allExpenses
            .Where(e => e.PaidByUserId.HasValue)
            .GroupBy(e => e.PaidByUserId!.Value)
            .Select(g =>
            {
                var pUserId = g.Key;
                var pName = userNameMap.GetValueOrDefault(pUserId, g.First().PayerName ?? "Roommate");
                var totalPaid = g.Sum(x => x.TotalAmount);
                var pendingReimbursement = g.Where(x => !x.IsReimbursed).Sum(x => x.TotalAmount);
                var reimbursedAmount = g.Where(x => x.IsReimbursed).Sum(x => x.TotalAmount);
                var pendingCount = g.Count(x => !x.IsReimbursed);
                var isFullyReimbursed = pendingReimbursement == 0;
                return new
                {
                    userId = pUserId,
                    userName = pName,
                    totalPaid,
                    pendingReimbursement,
                    reimbursedAmount,
                    isFullyReimbursed,
                    pendingCount,
                    expenseCount = g.Count(),
                    status = isFullyReimbursed ? "Reimbursed from Pool ✓" : $"Pending Reimbursement (₹{pendingReimbursement:N0})"
                };
            })
            .OrderByDescending(x => x.pendingReimbursement)
            .ThenByDescending(x => x.totalPaid)
            .ToList();

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

        var allExpensesWithDates = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .Select(e => new { e.ExpenseDate, e.TotalAmount })
            .ToListAsync();

        var monthlyGroups = allExpensesWithDates
            .GroupBy(e => e.ExpenseDate.ToString("yyyy-MM"))
            .Select(g => new
            {
                month = g.Key,
                total = g.Sum(x => x.TotalAmount)
            })
            .OrderBy(x => x.month)
            .TakeLast(6)
            .ToList();

        var maxMonth = monthlyGroups.Any() ? monthlyGroups.Max(m => m.total) : 0m;
        var monthlyBreakdown = monthlyGroups.Select(m => new
        {
            m.month,
            m.total,
            percentage = maxMonth > 0 ? Math.Round((double)(m.total / maxMonth) * 100, 1) : 0
        }).ToList();

        return new
        {
            isAdmin,
            pendingItems,
            currentBalance = contributed - spent,
            monthlyTarget = target,
            totalContributions = contributed,
            totalSpent = spent,
            pendingReimbursementsTotal,
            memberStatuses,
            categoryBreakdown,
            itemBreakdown,
            monthlyBreakdown,
            outOfPocketSummary,
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

        // Distribute target equally to all active members
        var activeMembers = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == MemberStatus.Active)
            .ToListAsync();

        if (activeMembers.Count > 0)
        {
            var sharePerMember = Math.Round(target / activeMembers.Count, 2);
            foreach (var m in activeMembers)
            {
                m.MonthlyPoolShare = sharePerMember;
            }
        }

        await _db.SaveChangesAsync();
        var perMember = activeMembers.Count > 0 ? Math.Round(target / activeMembers.Count, 2) : 0m;
        return (true, $"Monthly target set to ₹{target:N0} (₹{perMember:N0}/member)");
    }

    // ───────────── LEDGER HISTORY (daily, weekly, monthly, custom) ─────────────
    public async Task<object> GetHistoryAsync(int groupId, string? period, string? fromDateStr, string? toDateStr)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly? fromDate = null;
        DateOnly? toDate = null;

        if (period == "daily")
        {
            fromDate = today;
            toDate = today;
        }
        else if (period == "weekly")
        {
            fromDate = today.AddDays(-7);
            toDate = today;
        }
        else if (period == "monthly")
        {
            fromDate = new DateOnly(today.Year, today.Month, 1);
            toDate = today;
        }
        else if (period == "custom")
        {
            if (DateOnly.TryParse(fromDateStr, out var fd)) fromDate = fd;
            if (DateOnly.TryParse(toDateStr, out var td)) toDate = td;
        }

        // Fetch contributions
        var contribQuery = _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved);

        if (fromDate.HasValue) contribQuery = contribQuery.Where(c => c.ContributedOn >= fromDate.Value);
        if (toDate.HasValue) contribQuery = contribQuery.Where(c => c.ContributedOn <= toDate.Value);

        var contribsRaw = await contribQuery
            .OrderByDescending(c => c.ContributedOn).ThenByDescending(c => c.Id)
            .Select(c => new
            {
                id = "c" + c.Id,
                type = "Contribution",
                description = !string.IsNullOrWhiteSpace(c.Message)
                    ? c.Message
                    : ("Pool contribution" + (c.TransactionRef != null ? $" ({c.TransactionRef})" : "")),
                userName = c.User.FullName,
                date = c.ContributedOn.ToString("yyyy-MM-dd"),
                amount = c.Amount,
                status = "Approved",
                payerType = "member",
                payerName = c.User.FullName,
                recorderName = c.User.FullName,
                receiptUrl = (string?)null,
                category = (string?)null,
                isReimbursed = true
            })
            .ToListAsync();

        // Fetch expenses
        var expenseQuery = _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided);

        if (fromDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate >= fromDate.Value);
        if (toDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate <= toDate.Value);

        var expensesRaw = await expenseQuery
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.Description,
                e.TotalAmount,
                e.ExpenseDate,
                e.RecordedByUserId,
                e.PaidByUserId,
                e.PayerName,
                e.ReceiptUrl,
                e.Category,
                e.IsReimbursed,
                Items = e.Items.Select(i => new { i.Id, i.ItemName, i.Quantity, i.Amount }).ToList()
            })
            .ToListAsync();

        var involvedUserIds = expensesRaw
            .Select(e => e.RecordedByUserId)
            .Concat(expensesRaw.Where(e => e.PaidByUserId.HasValue).Select(e => e.PaidByUserId!.Value))
            .Distinct()
            .ToList();

        var userNameMap = await _db.Users
            .Where(u => involvedUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var mappedExpenses = expensesRaw.Select(e =>
        {
            var recorder = userNameMap.GetValueOrDefault(e.RecordedByUserId, "Roommate");
            var payer = e.PaidByUserId.HasValue
                ? userNameMap.GetValueOrDefault(e.PaidByUserId.Value, e.PayerName ?? "Roommate")
                : "Central Pool";

            var userDisplay = e.PaidByUserId.HasValue
                ? $"{payer} (Paid own money · {(e.IsReimbursed ? "Reimbursed from Pool ✓" : "Pending Reimbursement")})"
                : $"Central Pool (Added by {recorder})";

            return new
            {
                id = "e" + e.Id,
                type = "Expense",
                description = e.Description,
                userName = userDisplay,
                date = e.ExpenseDate.ToString("yyyy-MM-dd"),
                amount = e.TotalAmount,
                status = e.PaidByUserId.HasValue ? (e.IsReimbursed ? "Reimbursed ✓" : "Pending Reimbursement") : "Approved",
                payerType = e.PaidByUserId.HasValue ? "member" : "pool",
                payerName = payer,
                recorderName = recorder,
                receiptUrl = e.ReceiptUrl,
                category = e.Category,
                isReimbursed = e.IsReimbursed,
                items = (object?)e.Items
            };
        }).ToList();

        var allTransactions = contribsRaw.Select(c => new
        {
            c.id,
            c.type,
            c.description,
            c.userName,
            c.date,
            c.amount,
            c.status,
            c.payerType,
            c.payerName,
            c.recorderName,
            c.receiptUrl,
            c.category,
            c.isReimbursed,
            items = (object?)null
        })
        .Concat(mappedExpenses)
        .OrderByDescending(t => t.date)
        .ThenByDescending(t => t.id)
        .ToList();

        var totalIn = contribsRaw.Sum(c => c.amount);
        var totalOut = mappedExpenses.Sum(e => e.amount);

        return new
        {
            period = period ?? "monthly",
            fromDate = fromDate?.ToString("yyyy-MM-dd"),
            toDate = toDate?.ToString("yyyy-MM-dd"),
            totalIn,
            totalOut,
            netChange = totalIn - totalOut,
            totalCount = allTransactions.Count,
            transactions = allTransactions
        };
    }

    public async Task<(bool ok, string message, decimal reimbursedAmount)> ReimburseOutOfPocketAsync(int groupId, int currentUserId, ReimburseOutOfPocketDto dto)
    {
        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == currentUserId && m.Status == MemberStatus.Active);
        if (member == null) return (false, "You are not an active member of this group.", 0m);

        var isAdmin = member.Role == MemberRole.Admin;

        if (!isAdmin && dto.TargetUserId.HasValue && dto.TargetUserId.Value != currentUserId)
        {
            return (false, "Only group Admins can reimburse expenses for other roommates.", 0m);
        }

        var query = _db.PoolExpenses.Where(e => e.GroupId == groupId && !e.IsVoided && e.PaidByUserId.HasValue && !e.IsReimbursed);

        if (dto.ExpenseId.HasValue)
        {
            query = query.Where(e => e.Id == dto.ExpenseId.Value);
        }
        else if (dto.TargetUserId.HasValue)
        {
            query = query.Where(e => e.PaidByUserId == dto.TargetUserId.Value);
        }
        else if (!isAdmin)
        {
            query = query.Where(e => e.PaidByUserId == currentUserId);
        }

        var expensesToReimburse = await query.ToListAsync();
        if (!expensesToReimburse.Any())
        {
            return (false, "No pending out-of-pocket expenses found to reimburse.", 0m);
        }

        var totalReimbursement = expensesToReimburse.Sum(e => e.TotalAmount);

        var totalContributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;

        var totalAlreadySpent = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed))
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        var availableBalance = totalContributed - totalAlreadySpent;

        if (availableBalance < totalReimbursement)
        {
            return (false, $"Insufficient central room pool balance (₹{availableBalance:N0} available) to reimburse ₹{totalReimbursement:N0}. Roommates need to deposit contributions first.", 0m);
        }

        foreach (var exp in expensesToReimburse)
        {
            exp.IsReimbursed = true;
        }

        await _db.SaveChangesAsync();

        foreach (var exp in expensesToReimburse)
        {
            await _audit.LogAsync("PoolExpense", exp.Id, "ReimburseOutOfPocket", null, new { exp.TotalAmount, exp.PaidByUserId, ReimbursedByUserId = currentUserId }, currentUserId, "Reimbursed from Central Room Pool");
        }

        return (true, $"Successfully reimbursed ₹{totalReimbursement:N0} from central pool for {expensesToReimburse.Count} expense(s).", totalReimbursement);
    }
}