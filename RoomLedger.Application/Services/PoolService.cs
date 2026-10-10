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
    private readonly NotificationService _notifications;

    public PoolService(IApplicationDbContext db, IAuditService audit, NotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _notifications = notifications;
    }

    // ───────────────────── CONTRIBUTE TO POOL ─────────────────────
    public async Task<(bool ok, string message)> ContributeAsync(int groupId, int userId, ContributeDto dto)
    {
        if (dto.Amount <= 0) return (false, "Amount must be greater than zero");
        if (!await IsActiveMemberAsync(groupId, userId))
            return (false, "You are not an active member of this group");

        var isAdmin = await IsAdminAsync(groupId, userId);
        
        // Security check: Only Admin can record contributions for other members!
        if (!isAdmin && dto.MemberUserIds != null && dto.MemberUserIds.Any(id => id != userId))
        {
            return (false, "Only group Admins can record contributions for other members");
        }

        // Only Admin can auto-approve! Regular member contributions always require admin approval.
        var shouldApprove = isAdmin;
        var currentMonth = IndianTime.CurrentMonth;
        var today = IndianTime.Today;
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
                    Status = shouldApprove ? ContributionStatus.Approved : ContributionStatus.Pending,
                    ApprovedByUserId = shouldApprove ? userId : null,
                    ApprovedAt = shouldApprove ? IndianTime.Now : null
                });
            }

            await _db.SaveChangesAsync();
            return (true, shouldApprove
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
                Status = shouldApprove ? ContributionStatus.Approved : ContributionStatus.Pending,
                ApprovedByUserId = shouldApprove ? userId : null,
                ApprovedAt = shouldApprove ? IndianTime.Now : null
            });

            await _db.SaveChangesAsync();

            if (!shouldApprove)
            {
                var adminUserIds = await _db.GroupMembers
                    .Where(m => m.GroupId == groupId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active)
                    .Select(m => m.UserId)
                    .ToListAsync();
                var contributor = await _db.Users.FindAsync(userId);
                var contributorName = contributor?.FullName ?? "Roommate";
                foreach (var adminId in adminUserIds)
                {
                    if (adminId != userId)
                    {
                        _db.Notifications.Add(new Notification
                        {
                            UserId = adminId,
                            GroupId = groupId,
                            Title = "💰 Pool Contribution Approval Needed",
                            Message = $"{contributorName} submitted a pool contribution of ₹{dto.Amount:N2}. Please review and approve.",
                            Type = "pool_contribution_approval",
                            CreatedAt = IndianTime.Now
                        });
                    }
                }
                await _db.SaveChangesAsync();
            }

            return (true, shouldApprove ? $"Added ₹{dto.Amount:N0} to Pool" : "Contribution submitted — waiting for admin approval");
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
                ? d : IndianTime.Today,
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

        // Notify Admins for approval if expense was paid out-of-pocket
        if (paidByUserId.HasValue)
        {
            var adminUserIds = await _db.GroupMembers
                .Where(m => m.GroupId == groupId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active)
                .Select(m => m.UserId)
                .ToListAsync();

            foreach (var adminId in adminUserIds)
            {
                if (adminId != userId)
                {
                    _db.Notifications.Add(new Notification
                    {
                        UserId = adminId,
                        GroupId = groupId,
                        Title = "⚠️ Out-of-Pocket Expense Approval Needed",
                        Message = $"{payerName} spent ₹{total:N2} from their own pocket for '{expense.Description}'. Please review and approve reimbursement.",
                        Type = "pool_expense_approval",
                        CreatedAt = IndianTime.Now
                    });
                }
            }
            await _db.SaveChangesAsync();
        }

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
                        PaidAt = IndianTime.Now
                    });
                }
                await _db.SaveChangesAsync();
            }
        }

        // 2-Tier Low Pool Balance Milestone Alerts (<= 1k and <= 500)
        await CheckAndSendLowBalanceAlertAsync(groupId);

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
        var contributed = await _db.PoolContributions.Where(c => c.GroupId == groupId).SumAsync(c => (decimal?)c.Amount) ?? 0m;
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
        return await _db.ExpenseCategories.OrderBy(c => c.Name).Select(c => (object)new { c.Id, c.Name, c.Icon }).ToListAsync();
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

        // Enforce: only the last recorded pool expense can be deleted/voided
        var latestExpense = await _db.PoolExpenses
            .Where(x => x.GroupId == groupId && !x.IsVoided)
            .OrderByDescending(x => x.ExpenseDate)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync();

        if (latestExpense == null || latestExpense.Id != expenseId)
        {
            return (false, "Only the last recorded pool expense can be deleted. Earlier transactions are locked to preserve ledger integrity.");
        }

        var old = new { e.Description, e.TotalAmount };
        e.IsVoided = true;
        foreach (var item in e.Items) item.Amount = 0;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("PoolExpense", e.Id, "Void", old, null, userId, dto.Reason);
        return (true, "Pool expense voided — balance restored");
    }

    public async Task<(bool ok, string message)> EditExpenseAsync(int groupId, int userId, int expenseId, EditPoolExpenseDto dto)
    {
        if (!await IsAdminAsync(groupId, userId))
            return (false, "Only group Admin can edit pool expenses");

        if (string.IsNullOrWhiteSpace(dto.Description))
            return (false, "Expense description is required");

        var e = await _db.PoolExpenses.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == expenseId && x.GroupId == groupId);
        if (e == null) return (false, "Expense not found");
        if (e.IsVoided) return (false, "Cannot edit a voided expense. Please record a new expense if needed.");

        // Enforce: only the last recorded pool expense can be edited
        var latestExpense = await _db.PoolExpenses
            .Where(x => x.GroupId == groupId && !x.IsVoided)
            .OrderByDescending(x => x.ExpenseDate)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync();

        if (latestExpense == null || latestExpense.Id != expenseId)
        {
            return (false, "Only the last recorded pool expense can be edited. Earlier transactions are locked to preserve ledger integrity.");
        }

        var oldSnapshot = new
        {
            e.Description,
            e.TotalAmount,
            ExpenseDate = e.ExpenseDate.ToString("yyyy-MM-dd"),
            e.Category,
            ItemCount = e.Items.Count
        };

        // 1. Calculate new total: either from provided Items or direct Amount
        decimal newTotal = 0;
        if (dto.Items != null && dto.Items.Count > 0)
        {
            newTotal = dto.Items.Sum(i => i.Amount);
        }
        else if (dto.Amount.HasValue && dto.Amount.Value > 0)
        {
            newTotal = dto.Amount.Value;
        }
        else
        {
            newTotal = e.TotalAmount;
        }

        if (newTotal <= 0)
            return (false, "Expense amount must be greater than zero");

        e.Description = dto.Description.Trim();
        e.TotalAmount = newTotal;

        if (!string.IsNullOrWhiteSpace(dto.ExpenseDate) && DateOnly.TryParse(dto.ExpenseDate, out var parsedDate))
        {
            e.ExpenseDate = parsedDate;
        }

        if (!string.IsNullOrWhiteSpace(dto.Category))
        {
            e.Category = dto.Category.Trim();
        }

        if (dto.ReceiptUrl != null)
        {
            e.ReceiptUrl = string.IsNullOrWhiteSpace(dto.ReceiptUrl) ? null : dto.ReceiptUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.PayerType))
        {
            if (dto.PayerType == "pool")
            {
                e.PaidByUserId = null;
                e.PayerName = "Central Pool";
                e.IsReimbursed = true;
            }
            else if (dto.PayerType == "me" && dto.PaidByUserId.HasValue)
            {
                e.PaidByUserId = dto.PaidByUserId.Value;
                var user = await _db.Users.FindAsync(dto.PaidByUserId.Value);
                e.PayerName = user?.FullName ?? "Roommate";
            }
        }

        // 2. Update line items if provided
        if (dto.Items != null && dto.Items.Count > 0)
        {
            _db.ExpenseItems.RemoveRange(e.Items);
            e.Items = dto.Items.Select(i => new ExpenseItem
            {
                ExpenseCategoryId = i.CategoryId,
                ItemName = string.IsNullOrWhiteSpace(i.ItemName) ? e.Description : i.ItemName.Trim(),
                Quantity = i.Quantity ?? 1m,
                Amount = i.Amount
            }).ToList();
        }
        else if (e.Items.Count == 1)
        {
            var singleItem = e.Items.First();
            singleItem.ItemName = e.Description;
            singleItem.Amount = newTotal;
        }

        await _db.SaveChangesAsync();

        var newSnapshot = new
        {
            e.Description,
            e.TotalAmount,
            ExpenseDate = e.ExpenseDate.ToString("yyyy-MM-dd"),
            e.Category,
            ItemCount = e.Items.Count
        };

        await _audit.LogAsync("PoolExpense", e.Id, "Edit", oldSnapshot, newSnapshot, userId, dto.Reason ?? "Admin edited pool expense");

        return (true, $"Pool expense updated successfully. New total: ₹{newTotal:F2}");
    }

    public async Task<object?> GetExpenseByIdAsync(int groupId, int expenseId)
    {
        var e = await _db.PoolExpenses.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == expenseId && x.GroupId == groupId);

        if (e == null) return null;

        var isEdited = await _db.AuditLogs.AnyAsync(a => a.EntityName == "PoolExpense" && a.EntityId == e.Id && a.Action == "Edit");
        var isLatest = await _db.PoolExpenses
            .Where(x => x.GroupId == groupId && !x.IsVoided)
            .OrderByDescending(x => x.ExpenseDate)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .FirstOrDefaultAsync() == expenseId;

        var payer = e.PaidByUserId.HasValue
            ? await _db.Users.Where(u => u.Id == e.PaidByUserId.Value).Select(u => u.FullName).FirstOrDefaultAsync() ?? e.PayerName : "Central Pool";

        return new
        {
            id = e.Id,
            description = e.Description,
            totalAmount = e.TotalAmount,
            expenseDate = e.ExpenseDate.ToString("yyyy-MM-dd"),
            category = e.Category,
            paidByUserId = e.PaidByUserId,
            payerName = payer,
            payerType = e.PaidByUserId.HasValue ? "me" : "pool",
            receiptUrl = e.ReceiptUrl,
            isReimbursed = e.IsReimbursed,
            isVoided = e.IsVoided,
            isEdited,
            isLatest,
            items = e.Items.Select(i => new
            {
                id = i.Id,
                name = i.ItemName,
                qty = i.Quantity,
                price = i.Amount,
                categoryId = i.ExpenseCategoryId
            }).ToList()
        };
    }

    private async Task<bool> IsAdminAsync(int groupId, int userId) =>
        await _db.GroupMembers.AnyAsync(m =>
            m.GroupId == groupId && m.UserId == userId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);


    // PoolService — GetPoolBalanceAsync:
    public async Task<object> GetPoolBalanceAsync(int groupId, int userId, string? month = null)
    {
        var target = await _db.Groups
            .Where(g => g.Id == groupId)
            .Select(g => (decimal?)g.MonthlyPoolTarget ?? 0m)
            .FirstOrDefaultAsync();

        // 1. ALL-TIME CASH IN HAND (Live Physical Pool Wallet Balance)
        var totalContributionsAllTime = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;

        // Money actually withdrawn from pool: direct pool payments + reimbursed out-of-pocket expenses
        var totalSpentAllTime = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed))
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        var currentBalance = totalContributionsAllTime - totalSpentAllTime;

        var pendingReimbursementsTotal = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && e.PaidByUserId.HasValue && !e.IsReimbursed)
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

        // 2. PARSE SELECTED MONTH & CARRYOVER OPENING BALANCE
        var nowMonthStr = IndianTime.CurrentMonth;
        var selectedMonth = !string.IsNullOrWhiteSpace(month) ? month.Trim() : nowMonthStr;
        bool isAllTime = selectedMonth.Equals("all", StringComparison.OrdinalIgnoreCase);

        DateOnly? startOfMonth = null;
        DateOnly? endOfMonth = null;

        if (!isAllTime && DateOnly.TryParseExact(selectedMonth + "-01", "yyyy-MM-dd", out var parsedDate))
        {
            startOfMonth = parsedDate;
            endOfMonth = startOfMonth.Value.AddMonths(1).AddDays(-1);
        }
        else if (!isAllTime)
        {
            selectedMonth = nowMonthStr;
            startOfMonth = new DateOnly(IndianTime.Now.Year, IndianTime.Now.Month, 1);
            endOfMonth = startOfMonth.Value.AddMonths(1).AddDays(-1);
        }

        // Opening carryover balance entering this month (contributions before this month minus expenses before this month)
        decimal openingCarryover = 0m;
        if (startOfMonth.HasValue)
        {
            var contribsBefore = await _db.PoolContributions
                .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved && c.ContributedOn < startOfMonth.Value)
                .SumAsync(c => (decimal?)c.Amount) ?? 0m;

            var spentBefore = await _db.PoolExpenses
                .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed) && e.ExpenseDate < startOfMonth.Value)
                .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

            openingCarryover = contribsBefore - spentBefore;
        }

        // Month-specific contributions & expenses
        decimal monthContributions;
        decimal monthSpent;

        if (startOfMonth.HasValue && endOfMonth.HasValue)
        {
            monthContributions = await _db.PoolContributions
                .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved && c.PeriodMonth == selectedMonth)
                .SumAsync(c => (decimal?)c.Amount) ?? 0m;

            monthSpent = await _db.PoolExpenses
                .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed)
                            && e.ExpenseDate >= startOfMonth.Value && e.ExpenseDate <= endOfMonth.Value)
                .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;
        }
        else
        {
            monthContributions = totalContributionsAllTime;
            monthSpent = totalSpentAllTime;
        }

        var monthlyBudgetRemaining = Math.Max(0, target - monthSpent);
        var monthlySpentPercentage = target > 0 ? Math.Round((monthSpent / target) * 100, 1) : 0m;
        var monthlyCollectedPercentage = target > 0 ? Math.Round((monthContributions / target) * 100, 1) : 0m;

        // Distinct available months for filtering
        var contribMonths = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && !string.IsNullOrWhiteSpace(c.PeriodMonth))
            .Select(c => c.PeriodMonth)
            .Distinct()
            .ToListAsync();

        var allExpenseDates = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .Select(e => e.ExpenseDate)
            .ToListAsync();

        var expenseMonths = allExpenseDates
            .Select(d => d.ToString("yyyy-MM"))
            .Distinct()
            .ToList();

        var availableMonths = contribMonths.Concat(expenseMonths)
            .Append(nowMonthStr)
            .Distinct()
            .OrderByDescending(m => m)
            .ToList();

        // Members: no User nav on GroupMember → project id + share, join names via _db.Users
        var members = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == MemberStatus.Active)
            .Select(m => new
            {
                m.UserId,
                m.MonthlyPoolShare,
                m.Role,
                IsAlias = m.IsAlias ?? false,
                AliasName = m.AliasName ?? string.Empty
            }).ToListAsync();

        var realUserIds = members.Where(m => !m.IsAlias).Select(m => m.UserId).Distinct().ToList();
        var nameMap = await _db.Users
            .Where(u => realUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var memberIds = members.Select(m => m.UserId).ToList();

        // Selected month's contributions per member
        var monthSumsQuery = _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved);

        var pendingMonthSumsQuery = _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Pending);

        if (!isAllTime)
        {
            monthSumsQuery = monthSumsQuery.Where(c => c.PeriodMonth == selectedMonth);
            pendingMonthSumsQuery = pendingMonthSumsQuery.Where(c => c.PeriodMonth == selectedMonth);
        }

        var monthSums = await monthSumsQuery
            .GroupBy(c => c.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.UserId, x => x.Total);

        var pendingMonthSums = await pendingMonthSumsQuery
            .GroupBy(c => c.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.UserId, x => x.Total);

        var memberStatuses = members.Select(m =>
        {
            var rawContributed = monthSums.GetValueOrDefault(m.UserId);
            var contributedThisMonth = Math.Round(rawContributed, 2);
            var rawPendingApproval = pendingMonthSums.GetValueOrDefault(m.UserId);
            var pendingApprovalAmount = Math.Round(rawPendingApproval, 2);
            var expected = Math.Round(m.MonthlyPoolShare > 0
                ? m.MonthlyPoolShare
                : (members.Count > 0 ? (decimal)target / members.Count : 0m), 2);
            var pendingAmount = Math.Round(Math.Max(0, expected - contributedThisMonth), 2);

            return new
            {
                userId = m.UserId,
                userName = m.IsAlias ? (m.AliasName ?? "Unknown") : nameMap.GetValueOrDefault(m.UserId, "Unknown"),
                contributedThisMonth,
                expectedThisMonth = expected,
                hasPaidTarget = expected > 0 && contributedThisMonth >= expected,
                pendingAmount,
                pendingApprovalAmount,
                hasPendingApproval = pendingApprovalAmount > 0,
                isAlias = m.IsAlias,
                role = m.Role.ToString(),
                isAdmin = m.Role == MemberRole.Admin
            };
        }).OrderBy(m => m.hasPaidTarget ? 1 : 0).ThenByDescending(m => m.pendingAmount).ThenBy(m => m.userName).ToList();

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
            }).ToListAsync();

        var involvedUserIds = recentExpensesRaw
            .Select(e => e.RecordedByUserId)
            .Concat(recentExpensesRaw.Where(e => e.PaidByUserId.HasValue).Select(e => e.PaidByUserId!.Value))
            .Distinct()
            .ToList();

        var userNameMap = await _db.Users.Where(u => involvedUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var editedExpenseIds = await _db.AuditLogs
            .Where(a => a.EntityName == "PoolExpense" && a.Action == "Edit")
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync();
        var editedIdsSet = editedExpenseIds.ToHashSet();

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
                isEdited = editedIdsSet.Contains(e.Id),
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

        // Dynamic Category Breakdown & Out-of-Pocket Reimbursements (scoped to selected month)
        var expenseQuery = _db.PoolExpenses.Where(e => e.GroupId == groupId && !e.IsVoided);
        if (startOfMonth.HasValue && endOfMonth.HasValue)
        {
            expenseQuery = expenseQuery.Where(e => e.ExpenseDate >= startOfMonth.Value && e.ExpenseDate <= endOfMonth.Value);
        }

        var allExpenses = await expenseQuery
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
            }).OrderByDescending(i => i.total).ToList();

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
                var memberStat = memberStatuses.FirstOrDefault(m => m.userId == pUserId);
                var userPendingContribution = memberStat != null ? memberStat.pendingAmount : 0m;
                return new
                {
                    userId = pUserId,
                    userName = pName,
                    totalPaid,
                    pendingReimbursement,
                    reimbursedAmount,
                    isFullyReimbursed,
                    pendingCount,
                    userPendingContribution,
                    expenseCount = g.Count(),
                    status = isFullyReimbursed ? "Reimbursed from Pool ✓" : $"Pending Reimbursement (₹{pendingReimbursement:N0})"
                };
            })
            .OrderByDescending(x => x.pendingReimbursement)
            .ThenByDescending(x => x.totalPaid)
            .ToList();

        var isAdmin = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);

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

        var lowThreshold = 1000m;
        var isLowBalance = currentBalance <= 1000m;
        var isCriticalBalance = currentBalance <= 500m;

        return new
        {
            isAdmin,
            pendingItems,
            selectedMonth,
            availableMonths,
            isCurrentMonth = selectedMonth == nowMonthStr,

            // Live Physical Cash in Hand (Never resets on 1st of month)
            currentBalance,
            openingCarryover,
            totalContributionsAllTime,
            totalSpentAllTime,

            // Selected Month Budget & Spending (Scenario 2: Fixed Monthly Cycle)
            monthlyTarget = target,
            monthContributions,
            monthSpent,
            monthlyBudgetRemaining,
            monthlySpentPercentage,
            monthlyCollectedPercentage,

            // Compatibility properties
            totalContributions = totalContributionsAllTime,
            totalSpent = totalSpentAllTime,

            isLowBalance,
            isCriticalBalance,
            lowThreshold,
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
        c.ApprovedAt = IndianTime.Now;
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
        c.ApprovedAt = IndianTime.Now;
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
        var today = IndianTime.Today;
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
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.CreatedAt,
                c.ContributedOn,
                type = "Contribution",
                description = !string.IsNullOrWhiteSpace(c.Message)
                    ? c.Message
                    : ("Pool contribution" + (c.TransactionRef != null ? $" ({c.TransactionRef})" : "")),
                userName = c.User.FullName,
                date = c.CreatedAt.ToString("o"),
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
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new
            {
                e.Id,
                e.CreatedAt,
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

        var userNameMap = await _db.Users.Where(u => involvedUserIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);

        var historyEditedIds = await _db.AuditLogs
            .Where(a => a.EntityName == "PoolExpense" && a.Action == "Edit")
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync();
        var historyEditedSet = historyEditedIds.ToHashSet();

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
                date = e.CreatedAt.ToString("o"),
                amount = e.TotalAmount,
                status = e.PaidByUserId.HasValue ? (e.IsReimbursed ? "Reimbursed ✓" : "Pending Reimbursement") : "Approved",
                payerType = e.PaidByUserId.HasValue ? "member" : "pool",
                payerName = payer,
                recorderName = recorder,
                receiptUrl = e.ReceiptUrl,
                category = e.Category,
                isReimbursed = e.IsReimbursed,
                isEdited = historyEditedSet.Contains(e.Id),
                createdAt = e.CreatedAt,
                items = (object?)e.Items
            };
        }).ToList();

        var allTransactions = contribsRaw.Select(c => new
        {
            id = "c" + c.Id,
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
            createdAt = c.CreatedAt,
            items = (object?)null
        })
        .Concat(mappedExpenses.Select(e => new
        {
            e.id,
            e.type,
            e.description,
            e.userName,
            e.date,
            e.amount,
            e.status,
            e.payerType,
            e.payerName,
            e.recorderName,
            e.receiptUrl,
            e.category,
            e.isReimbursed,
            createdAt = e.createdAt,
            items = e.items
        }))
        .OrderByDescending(t => t.createdAt)
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
        if (!isAdmin)
        {
            return (false, "Only group Admins can approve and reimburse out-of-pocket roommate expenses.", 0m);
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

        var expensesToReimburse = await query.ToListAsync();
        if (!expensesToReimburse.Any())
        {
            return (false, "No pending out-of-pocket expenses found to reimburse.", 0m);
        }

        var totalReimbursement = expensesToReimburse.Sum(e => e.TotalAmount);
        var isCutContribution = string.Equals(dto.Mode, "cut_contribution", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(dto.Mode, "cut", StringComparison.OrdinalIgnoreCase);

        if (isCutContribution)
        {
            // Mode: Cut / Offset from Member's Monthly Pool Contribution
            // Settle out-of-pocket expenses and credit an approved contribution for this roommate in the current month.
            var targetUserId = expensesToReimburse.First().PaidByUserId!.Value;
            var currentMonth = IndianTime.CurrentMonth;
            var today = IndianTime.Today;

            foreach (var exp in expensesToReimburse)
            {
                exp.IsReimbursed = true;
            }

            var offsetContribution = new PoolContribution
            {
                GroupId = groupId,
                UserId = targetUserId,
                Amount = totalReimbursement,
                ContributedOn = today,
                PeriodMonth = currentMonth,
                TransactionRef = "OUT-OF-POCKET-OFFSET",
                Message = $"Offset from out-of-pocket expense ({expensesToReimburse.Count} item(s))",
                Status = ContributionStatus.Approved,
                ApprovedByUserId = currentUserId,
                ApprovedAt = IndianTime.Now
            };

            _db.PoolContributions.Add(offsetContribution);
            await _db.SaveChangesAsync();

            foreach (var exp in expensesToReimburse)
            {
                await _audit.LogAsync("PoolExpense", exp.Id, "OffsetExpense", null, new { exp.TotalAmount, exp.PaidByUserId, SettledByUserId = currentUserId, Mode = "cut_contribution" }, currentUserId, "Offset against Monthly Pool Contribution");
            }

            var userName = await _db.Users.Where(u => u.Id == targetUserId).Select(u => u.FullName).FirstOrDefaultAsync() ?? "Roommate";
            return (true, $"Successfully cut ₹{totalReimbursement:N0} from {userName}'s monthly contribution and marked expenses as settled.", totalReimbursement);
        }
        else
        {
            // Mode: Return Cash from Central Room Pool
            var totalContributed = await _db.PoolContributions
                .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
                .SumAsync(c => (decimal?)c.Amount) ?? 0m;

            var totalAlreadySpent = await _db.PoolExpenses
                .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed))
                .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;

            var availableBalance = totalContributed - totalAlreadySpent;

            if (availableBalance < totalReimbursement)
            {
                return (false, $"Insufficient central room pool balance (₹{availableBalance:N0} available) to reimburse ₹{totalReimbursement:N0}. Roommates need to deposit contributions first, or choose 'Cut from Contribution'.", 0m);
            }

            foreach (var exp in expensesToReimburse)
            {
                exp.IsReimbursed = true;
            }

            await _db.SaveChangesAsync();

            // Check if cash payout caused pool to drop <= 1k or <= 500
            await CheckAndSendLowBalanceAlertAsync(groupId);

            foreach (var exp in expensesToReimburse)
            {
                await _audit.LogAsync("PoolExpense", exp.Id, "ReimburseOutOfPocket", null, new { exp.TotalAmount, exp.PaidByUserId, ReimbursedByUserId = currentUserId, Mode = "cash" }, currentUserId, "Reimbursed from Central Room Pool");
            }

            return (true, $"Successfully returned ₹{totalReimbursement:N0} from central pool for {expensesToReimburse.Count} expense(s).", totalReimbursement);
        }
    }

    // ───────────── LOW POOL FUND BALANCE 2-TIER ALERTS (<= 1k and <= 500) ─────────────
    private async Task CheckAndSendLowBalanceAlertAsync(int groupId)
    {
        // 1. Calculate Live Physical Cash in Hand
        var totalContributed = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;
        var totalSpent = await _db.PoolExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided && (!e.PaidByUserId.HasValue || e.IsReimbursed))
            .SumAsync(e => (decimal?)e.TotalAmount) ?? 0m;
        var currentBalance = totalContributed - totalSpent;

        // If balance is healthy (> 1,000), no low alert needed
        if (currentBalance > 1000m) return;

        // Cycle tracking: resets whenever someone adds money to the pool
        var lastContributionTime = await _db.PoolContributions
            .Where(c => c.GroupId == groupId && c.Status == ContributionStatus.Approved)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (DateTime?)c.CreatedAt)
            .FirstOrDefaultAsync() ?? DateTime.MinValue;

        // Also allow re-alerting if 48 hours have passed with pool still depleted
        var cycleCutoff = lastContributionTime > IndianTime.Now.AddHours(-48)
            ? lastContributionTime
            : IndianTime.Now.AddHours(-48);

        string? alertType = null;
        string? title = null;
        string? alertText = null;

        if (currentBalance <= 500m)
        {
            // Milestone 2: Critical Tier (<= 500)
            var alreadyNotifiedCritical = await _db.Notifications.AnyAsync(n =>
                n.GroupId == groupId &&
                n.Type == "pool_low_500" &&
                n.CreatedAt >= cycleCutoff);

            if (!alreadyNotifiedCritical)
            {
                alertType = "pool_low_500";
                title = "🚨 Critical Pool Fund Alert";
                alertText = $"Urgent: Central Room Pool has dropped to ₹{currentBalance:N2} (below ₹500 safety floor). Immediate roommate contributions are required to cover flat bills and essentials!";
            }
        }
        else if (currentBalance <= 1000m)
        {
            // Milestone 1: Warning Tier (<= 1,000)
            var alreadyNotifiedWarning = await _db.Notifications.AnyAsync(n =>
                n.GroupId == groupId &&
                (n.Type == "pool_low_1000" || n.Type == "pool_low_500" || n.Type == "pool_low_balance") &&
                n.CreatedAt >= cycleCutoff);

            if (!alreadyNotifiedWarning)
            {
                alertType = "pool_low_1000";
                title = "⚠️ Low Pool Fund Balance Warning";
                alertText = $"The Central Room Pool has dropped to ₹{currentBalance:N2} (below ₹1,000). Please contribute soon to cover upcoming daily expenses.";
            }
        }

        if (alertType != null && title != null && alertText != null)
        {
            var activeMemberIds = await _db.GroupMembers
                .Where(m => m.GroupId == groupId && m.Status == MemberStatus.Active)
                .Select(m => m.UserId)
                .ToListAsync();

            foreach (var memberId in activeMemberIds)
            {
                try
                {
                    await _notifications.PushAsync(
                        memberId,
                        groupId,
                        title,
                        alertText,
                        alertType,
                        $"/g/{groupId}/pool"
                    );
                }
                catch
                {
                    // Notification push failure should not abort the flow
                }
            }
        }
    }
}