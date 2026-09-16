using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Services;

public class IouService
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly NotificationService _notifications;
    public IouService(IApplicationDbContext db, IAuditService audit,NotificationService notificationService)
    {
        _db = db;
        _audit = audit;
        _notifications = notificationService;
    }

    // ───────────────────────────── ADD IOU EXPENSE ─────────────────────────────
    public async Task<(bool ok, string message)> AddExpenseAsync(int groupId, int payerId, IouExpenseDto dto)
    {
        var memberIds = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == Domain.Common.MemberStatus.Active)
            .Select(m => m.UserId).ToListAsync();

        if (!memberIds.Contains(payerId))
            return (false, "You are not an active member of this group");
        if (dto.Participants == null || dto.Participants.Count == 0)
            return (false, "Select at least one participant");
        if (dto.Participants.Any(p => !memberIds.Contains(p.UserId)))
            return (false, "All participants must be group members");
        if (dto.Amount <= 0)
            return (false, "Amount must be greater than zero");
        if (dto.Participants.Select(p => p.UserId).Distinct().Count() != dto.Participants.Count)
            return (false, "Duplicate participants not allowed");

        var custom = dto.Participants.Where(p => p.ShareAmount.HasValue).ToList();
        if (custom.Count > 0 && custom.Count < dto.Participants.Count)
            return (false, "Either all participants need custom shares, or none (equal split)");

        if (custom.Count > 0)
        {
            var shareSum = custom.Sum(p => p.ShareAmount!.Value);
            if (shareSum != dto.Amount)
                return (false, $"Custom shares total {shareSum} but expense is {dto.Amount} — they must match");
        }

        var expense = new IouExpense
        {
            GroupId = groupId,
            PaidById = payerId,
            Description = dto.Description,
            Amount = dto.Amount,
            ExpenseDate = DateOnly.TryParse(dto.ExpenseDate, out var d)
                ? d : DateOnly.FromDateTime(DateTime.UtcNow)
        };
        expense.Participants = dto.Participants.Select(p => new IouParticipant
        {
            UserId = p.UserId,
            ShareAmount = p.ShareAmount   // null = equal split
        }).ToList();

        _db.IouExpenses.Add(expense);
        await _db.SaveChangesAsync();
        return (true, "Expense added");
    }


    // ───────────────────── BALANCES + DEBT SIMPLIFICATION ─────────────────────
    // Returns the MINIMIZED transfer list: who should pay whom, and how much.
    public async Task<List<TransferDto>> GetSimplifiedDebtsAsync(int groupId)
    {
        var net = await BuildNetMatrixAsync(groupId);   // net[debtorId][creditorId] = amount owed

        // Greedy minimization
        var balance = new Dictionary<int, decimal>();   // + = is owed (creditor), − = owes (debtor)
        foreach (var (_, debtor, creditor, amount) in net)
        {
            balance[debtor] = balance.GetValueOrDefault(debtor) - amount;
            balance[creditor] = balance.GetValueOrDefault(creditor) + amount;
        }

        var debtors = new SortedSet<(decimal amt, int id)>();   // ascending → biggest debtor last
        var creditors = new SortedSet<(decimal amt, int id)>();
        foreach (var (id, bal) in balance)
        {
            if (bal < -0.01m) debtors.Add((-bal, id));
            else if (bal > 0.01m) creditors.Add((bal, id));
        }

        var transfers = new List<TransferDto>();
        while (debtors.Count > 0 && creditors.Count > 0)
        {
            var (dAmt, dId) = debtors.Max;      // biggest debtor
            var (cAmt, cId) = creditors.Max;    // biggest creditor
            debtors.Remove((dAmt, dId));
            creditors.Remove((cAmt, cId));

            var pay = Math.Min(dAmt, cAmt);
            transfers.Add(new TransferDto(dId, cId, Math.Round(pay, 2)));

            if (dAmt - pay > 0.01m) debtors.Add((dAmt - pay, dId));
            if (cAmt - pay > 0.01m) creditors.Add((cAmt - pay, cId));
        }
        return transfers;
    }

    // ───────────────── WHO-OWES-WHO matrix (for the UI debt view) ─────────────
    public async Task<List<DebtPairDto>> GetDebtMatrixAsync(int groupId)
    {
        var net = await BuildNetMatrixAsync(groupId);
        return net.Select(n => new DebtPairDto(n.debtor, n.creditor, Math.Round(n.amount, 2)))
                  .Where(x => x.Amount > 0.01m)
                  .OrderByDescending(x => x.Amount)
                  .ToList();
    }

    // Core: raw debts from expenses MINUS settlements, netted per pair
    private async Task<List<(int _, int debtor, int creditor, decimal amount)>> BuildNetMatrixAsync(int groupId)
    {
        // participant counts per expense (one query)
        var counts = await _db.IouParticipants
            .GroupBy(p => p.IouExpenseId)
            .Select(g => new { IouExpenseId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.IouExpenseId, x => x.Count);

        // raw debts: each participant owes the payer
        var owed = new Dictionary<(int from, int to), decimal>();
        var expenses = await _db.IouExpenses
            .Include(e => e.Participants)
            .Include(e => e.Items).ThenInclude(i => i.Assignments)
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .ToListAsync();

        foreach (var e in expenses)
        {
            foreach (var item in e.Items)
            {
                var eaters = item.Assignments.Select(a => a.UserId).ToList();
                if (eaters.Count == 0) continue;
                var perEater = Math.Round(item.Amount / eaters.Count, 2, MidpointRounding.AwayFromZero);
                foreach (var eaterId in eaters)
                {
                    if (eaterId == e.PaidById) continue;
                    owed[(eaterId, e.PaidById)] = owed.GetValueOrDefault((eaterId, e.PaidById)) + perEater;
                }
            }
            foreach (var p in e.Participants)
            {
                if (p.UserId == e.PaidById) continue;
                var count = counts.GetValueOrDefault(e.Id, e.Participants?.Count ?? 1);
                if (count <= 0) count = 1;
                var share = p.ShareAmount
                         ?? Math.Round(e.Amount / count, 2, MidpointRounding.AwayFromZero);
                owed[(p.UserId, e.PaidById)] = owed.GetValueOrDefault((p.UserId, e.PaidById)) + share;
            }
        }

        // settlements reduce debt (payer of settlement = debtor)
        var settlements = await _db.IouSettlements
            .Where(s => s.GroupId == groupId)
            .ToListAsync();
        foreach (var s in settlements)
            owed[(s.PayerId, s.PayeeId)] = owed.GetValueOrDefault((s.PayerId, s.PayeeId)) - s.Amount;

        // net both directions: A owes B − B owes A
        var result = new List<(int, int, int, decimal)>();
        foreach (var key in owed.Keys.ToList())
        {
            if (key.from >= key.to) continue;   // process each pair once
            var ab = owed.GetValueOrDefault(key);                       // from→to
            var ba = owed.GetValueOrDefault((key.to, key.from));        // to→from
            var net = ab - ba;
            if (net > 0.01m) result.Add((0, key.from, key.to, net));    //from owes to
            if (net < -0.01m) result.Add((0, key.to, key.from, -net));  // to owes from
        }
        return result;
    }

    // ───────────────────────────── SETTLE UP ─────────────────────────────
    public async Task<(bool ok, string message)> SettleUpAsync(int groupId, int fromUserId, SettleUpDto dto)
    {
        if (dto.Amount <= 0) return (false, "Amount must be greater than zero");

        _db.IouSettlements.Add(new IouSettlement
        {
            GroupId = groupId,
            PayerId = fromUserId,          // who owed
            PayeeId = dto.PayeeId,         // who is owed
            Amount = dto.Amount,
            SettledOn = DateOnly.FromDateTime(DateTime.UtcNow),
            TransactionRef = dto.TransactionRef
        });
        await _db.SaveChangesAsync();
        return (true, "Settlement recorded");
    }

    // ─────────────────── MY PERSONAL BALANCE (dashboard card) ───────────────────
    public async Task<object> GetMyBalanceAsync(int groupId, int userId)
    {
        var matrix = await GetDebtMatrixAsync(groupId);

        var iOwe = matrix.Where(d => d.DebtorId == userId).ToList();
        var owedToMe = matrix.Where(d => d.CreditorId == userId).ToList();

        var ids = iOwe.Select(d => d.CreditorId)
            .Concat(owedToMe.Select(d => d.DebtorId))
            .Distinct().ToList();
        var users = await _db.Users
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u);

        var totalOwedToMe = owedToMe.Sum(d => d.Amount);
        var totalIOwe = iOwe.Sum(d => d.Amount);

        return new
        {
            netBalance = totalOwedToMe - totalIOwe,
            youAreOwed = totalOwedToMe,
            youOwe = totalIOwe,
            iOwe = iOwe.Select(d => {
                var u = users.GetValueOrDefault(d.CreditorId);
                var upi = !string.IsNullOrWhiteSpace(u?.Phone) 
                    ? $"{u.Phone}@upi" 
                    : (!string.IsNullOrWhiteSpace(u?.Email) ? $"{u.Email.Split('@')[0]}@okaxis" : "roommate@upi");
                return new {
                    toUserId = d.CreditorId,
                    toUserName = u?.FullName ?? "Roommate",
                    amount = d.Amount,
                    upiId = upi
                };
            }),
            owedToMe = owedToMe.Select(d => {
                var u = users.GetValueOrDefault(d.DebtorId);
                var upi = !string.IsNullOrWhiteSpace(u?.Phone) 
                    ? $"{u.Phone}@upi" 
                    : (!string.IsNullOrWhiteSpace(u?.Email) ? $"{u.Email.Split('@')[0]}@okaxis" : "roommate@upi");
                return new {
                    fromUserId = d.DebtorId,
                    fromUserName = u?.FullName ?? "Roommate",
                    amount = d.Amount,
                    upiId = upi
                };
            })
        };
    }

    // ─────────────────── SEND IOU REMINDER TO DEBTOR ───────────────────
    public async Task<(bool ok, string message)> RemindDebtorAsync(int groupId, int creditorId, int debtorId)
    {
        var matrix = await GetDebtMatrixAsync(groupId);
        var debt = matrix.FirstOrDefault(d => d.DebtorId == debtorId && d.CreditorId == creditorId);
        if (debt == null || debt.Amount <= 0)
            return (false, "No outstanding debt found for this member");

        var creditor = await _db.Users.FindAsync(creditorId);
        var creditorName = creditor?.FullName ?? "Your flatmate";

        await _notifications.PushAsync(
            debtorId,
            groupId,
            "IOU Payment Reminder",
            $"{creditorName} sent a friendly reminder to settle ₹{debt.Amount:0.##} for shared expenses.",
            "IouReminder"
        );

        return (true, "Reminder sent successfully");
    }
    public async Task<(bool ok, string message)> VoidExpenseAsync(int groupId, int userId, int expenseId, VoidDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason))
            return (false, "Reason is required");

        var e = await _db.IouExpenses
            .Include(x => x.Participants)
            .FirstOrDefaultAsync(x => x.Id == expenseId && x.GroupId == groupId);
        if (e == null) return (false, "Expense not found");

        if (!await IsAdminOrPayerAsync(groupId, userId, e.PaidById))
            return (false, "Only group Admin or the original payer can void this expense");
        if (e.IsVoided) return (false, "Already voided");

        var old = new { e.Description, e.Amount, e.PaidById };
        e.IsVoided = true;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("IouExpense", e.Id, "Void", old, null, userId, dto.Reason);
        return (true, "Expense voided — removed from debt calculations");
    }

    // Edit description/amount (debts auto-recalculate from the matrix)
    public async Task<(bool ok, string message)> EditExpenseAsync(int groupId, int userId, int expenseId, EditIouDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason))
            return (false, "Reason is required");

        var e = await _db.IouExpenses.FirstOrDefaultAsync(x => x.Id == expenseId && x.GroupId == groupId);
        if (e == null) return (false, "Expense not found");
        if (!await IsAdminOrPayerAsync(groupId, userId, e.PaidById))
            return (false, "Only group Admin or the original payer can edit this expense");
        if (e.IsVoided) return (false, "Voided expenses cannot be edited");
        if (dto.Amount <= 0) return (false, "Amount must be greater than zero");

        var old = new { e.Description, e.Amount };
        e.Description = dto.Description;
        e.Amount = dto.Amount;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("IouExpense", e.Id, "Edit", old,
            new { e.Description, e.Amount }, userId, dto.Reason);
        return (true, "Expense updated");
    }

    private async Task<bool> IsAdminOrPayerAsync(int groupId, int userId, int payerId) =>
        userId == payerId ||
        await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId &&
            m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);
    // ───────────── EXPENSE HISTORY for a group ─────────────
    public async Task<object> GetExpensesAsync(int groupId)
    {
        var expenses = await _db.IouExpenses
            .Where(e => e.GroupId == groupId && !e.IsVoided)
            .OrderByDescending(e => e.CreatedAt).Take(50)
            .Select(e => new
            {
                e.Id,
                e.Description,
                e.Amount,
                e.ExpenseDate,
                paidBy = e.PaidById,
                e.IsVoided,
                splitType = e.Participants.Any(p => p.ShareAmount != null)
                            ? "custom" : "equal",
                participants = e.Participants.Select(p => new
                {
                    p.UserId,
                    p.ShareAmount    // null = equal share
                }),
                items = e.Items.Select(it => new
                {
                    it.ItemName,
                    it.Amount,
                    eaters = it.Assignments.Select(a => a.UserId)
                })
            })
            .ToListAsync();

        return expenses;
    }
    // ───────────── ITEMIZED SPLIT: assign each item to its eaters ─────────────
    public async Task<(bool ok, string message)> AddItemizedExpenseAsync(int groupId, int payerId, IouItemizedDto dto)
    {
        var memberIds = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == Domain.Common.MemberStatus.Active)
            .Select(m => m.UserId).ToListAsync();

        if (!memberIds.Contains(payerId))
            return (false, "You are not an active member of this group");
        if (dto.Items == null || dto.Items.Count == 0)
            return (false, "Add at least one item");
        if (dto.Amount <= 0)
            return (false, "Amount must be greater than zero");

        // STRICT: sum of items must equal the bill total (same rule as pool)
        var itemSum = dto.Items.Sum(i => i.Amount);
        if (itemSum != dto.Amount)
            return (false, $"Items total {itemSum} but bill is {dto.Amount} — they must match");

        foreach (var item in dto.Items)
        {
            if (item.EaterUserIds == null || item.EaterUserIds.Count == 0)
                return (false, $"Item '{item.ItemName}' needs at least one eater");
            if (item.EaterUserIds.Any(id => !memberIds.Contains(id)))
                return (false, $"Item '{item.ItemName}' has a non-member eater");
        }

        var expense = new IouExpense
        {
            GroupId = groupId,
            PaidById = payerId,
            Description = dto.Description,
            Amount = dto.Amount,
            ExpenseDate = DateOnly.TryParse(dto.ExpenseDate, out var d)
                ? d : DateOnly.FromDateTime(DateTime.UtcNow)
        };

        foreach (var i in dto.Items)
        {
            var item = new IouItem { ItemName = i.ItemName, Amount = i.Amount };
            item.Assignments = i.EaterUserIds.Select(uid => new IouItemAssignment
            { UserId = uid }).ToList();
            expense.Items.Add(item);
        }

        _db.IouExpenses.Add(expense);
        await _db.SaveChangesAsync();

        foreach (var eaterId in dto.Items.SelectMany(i => i.EaterUserIds).Distinct().Where(id => id != payerId))
            await _notifications.PushAsync(eaterId, groupId, "NewU expense",
                $"{dto.Description} — you've been included", "Settlement");
        return (true, "Itemized expense added");
    }
}
