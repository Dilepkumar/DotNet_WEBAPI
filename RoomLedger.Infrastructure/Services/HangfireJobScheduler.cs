using Hangfire;
using RoomLedger.Application.Services;
using RoomLedger.Domain.Common;

namespace RoomLedger.Infrastructure.Services;

public static class HangfireJobScheduler
{
    public static void ConfigureRecurringJobs()
    {
        // 1. Electricity Bill Monitoring: runs twice a day (10:00 AM and 10:00 PM IST)
        RecurringJob.AddOrUpdate<ElectricityBillService>(
            "electricity-bill-monitoring",
            service => service.ProcessDueAccountsForMonitoringJobAsync(CancellationToken.None),
            "0 10,22 * * *",
            new RecurringJobOptions { TimeZone = IndianTime.Zone });

        // 2. Database Cleanup: runs once daily at 2:00 AM IST
        RecurringJob.AddOrUpdate<IDatabaseCleanupService>(
            "database-cleanup",
            service => service.DoCleanupAsync(CancellationToken.None),
            "0 2 * * *",
            new RecurringJobOptions { TimeZone = IndianTime.Zone });

        // Enqueue an immediate cleanup run on startup to purge existing backlog
        BackgroundJob.Enqueue<IDatabaseCleanupService>(
            service => service.DoCleanupAsync(CancellationToken.None));
    }
}
