using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.Infrastructure.Services;

public interface IDatabaseCleanupService
{
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

    public async Task DoCleanupAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Hangfire Database Cleanup Job starting...");

        var now = DateTime.UtcNow;

        // 1. Delete expired or already used OTP codes
        var expiredOtps = await _db.OtpCodes.Where(o => o.ExpiresAt < now || o.IsUsed).ToListAsync(cancellationToken);

        if (expiredOtps.Count > 0)
        {
            _db.OtpCodes.RemoveRange(expiredOtps);
        }

        // 2. Delete expired refresh tokens OR revoked tokens older than 7 days
        var staleRevokedCutoff = now.AddDays(-7);
        var staleTokens = await _db.RefreshTokens.Where(r => r.ExpiresAt < now || (r.RevokedAt != null && r.RevokedAt < staleRevokedCutoff)).ToListAsync(cancellationToken);

        if (staleTokens.Count > 0)
        {
            _db.RefreshTokens.RemoveRange(staleTokens);
        }

        if (expiredOtps.Count > 0 || staleTokens.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Database cleanup executed: removed {OtpCount} expired OTPs and {TokenCount} stale refresh tokens.",
                expiredOtps.Count, staleTokens.Count);
        }
        else
        {
            _logger.LogInformation("Database cleanup completed: no expired OTPs or stale refresh tokens to purge.");
        }
    }
}
