using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<IouExpense> IouExpenses => Set<IouExpense>();
    public DbSet<IouExpenseShare> IouExpenseShares => Set<IouExpenseShare>();
    public DbSet<IouParticipant> IouParticipants => Set<IouParticipant>();
    public DbSet<IouSettlement> IouSettlements => Set<IouSettlement>();
    public DbSet<Settlement> Settlements => Set<Settlement>();               
    public DbSet<RecurringBill> RecurringBills => Set<RecurringBill>();
    public DbSet<BillSplit> BillSplits => Set<BillSplit>();
    public DbSet<PoolContribution> PoolContributions => Set<PoolContribution>();
    public DbSet<PoolExpense> PoolExpenses => Set<PoolExpense>();
    public DbSet<ExpenseItem> ExpenseItems => Set<ExpenseItem>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IouItem> IouItems => Set<IouItem>();
    public DbSet<IouItemAssignment> IouItemAssignments => Set<IouItemAssignment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        => base.SaveChangesAsync(ct);
}
