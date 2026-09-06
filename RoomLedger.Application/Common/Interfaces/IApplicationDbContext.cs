using Microsoft.EntityFrameworkCore;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<OtpCode> OtpCodes { get; }
    DbSet<Group> Groups { get; }
    DbSet<GroupMember> GroupMembers { get; }
    DbSet<IouExpense> IouExpenses { get; }
    DbSet<IouExpenseShare> IouExpenseShares { get; }
    DbSet<IouParticipant> IouParticipants { get; }
    DbSet<IouSettlement> IouSettlements { get; }
    DbSet<Settlement> Settlements { get; }
    DbSet<RecurringBill> RecurringBills { get; }
    DbSet<BillSplit> BillSplits { get; }
    DbSet<PoolContribution> PoolContributions { get; }
    DbSet<PoolExpense> PoolExpenses { get; }
    DbSet<ExpenseItem> ExpenseItems { get; }
    DbSet<ExpenseCategory> ExpenseCategories { get; }
    DbSet<AuditLog> AuditLogs { get; }
    public DbSet<IouItem> IouItems { get; }
    public DbSet<IouItemAssignment> IouItemAssignments { get; }
    public DbSet<Notification> Notifications { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
