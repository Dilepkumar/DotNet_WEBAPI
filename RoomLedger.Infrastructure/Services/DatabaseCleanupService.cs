using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.Infrastructure.Services;

public interface IDatabaseCleanupService
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 2)]
    Task DoCleanupAsync(CancellationToken cancellationToken = default);
}

public class DatabaseCleanupService : IDatabaseCleanupService
{
    private readonly IApplicationDbContext _db;
    private readonly ILogger<DatabaseCleanupService> _logger;

    public DatabaseCleanupService(IApplicationDbContext db, ILogger<DatabaseCleanupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 2)]
    public async Task DoCleanupAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Hangfire Database Cleanup Job starting...");

        var now = DateTime.UtcNow;
        var staleRevokedCutoff = now.AddDays(-7);

        try
        {
            // Direct set-based SQL deletion: avoids loading entities into memory, avoids change tracking,
            // and eliminates DbUpdateConcurrencyException when records are deleted concurrently.
            var deletedOtps = await _db.OtpCodes.Where(o => o.ExpiresAt < now || o.IsUsed).ExecuteDeleteAsync(cancellationToken);

            var deletedTokens = await _db.RefreshTokens.Where(r => r.ExpiresAt < now || (r.RevokedAt != null && r.RevokedAt < staleRevokedCutoff)).ExecuteDeleteAsync(cancellationToken);

            if (deletedOtps > 0 || deletedTokens > 0)
            {
                _logger.LogInformation("Database cleanup executed: removed {OtpCount} expired OTPs and {TokenCount} stale refresh tokens.",
                    deletedOtps, deletedTokens);
            }
            else
            {
                _logger.LogInformation("Database cleanup completed: no expired OTPs or stale refresh tokens to purge.");
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict during database cleanup; records were already modified or purged.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute database cleanup job.");
            throw;
        }
    }
}
