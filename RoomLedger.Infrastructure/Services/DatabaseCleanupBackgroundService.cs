using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.Infrastructure.Services;

public class DatabaseCleanupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabaseCleanupBackgroundService> _logger;
    private readonly TimeSpan _period = TimeSpan.FromDays(1);

    public DatabaseCleanupBackgroundService(IServiceProvider serviceProvider, ILogger<DatabaseCleanupBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Database Cleanup Background Service started.");

        // Initial delay so app startup is completely finished
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DoCleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error occurred during database maintenance cleanup.");
            }
            await Task.Delay(_period, stoppingToken);
        }
    }

    private async Task DoCleanupAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var now = DateTime.UtcNow;

        // 1. Delete expired or already used OTP codes
        var expiredOtps = await db.OtpCodes.Where(o => o.ExpiresAt < now || o.IsUsed).ToListAsync(stoppingToken);

        if (expiredOtps.Count > 0)
        {
            db.OtpCodes.RemoveRange(expiredOtps);
        }

        // 2. Delete revoked tokens older than 14 days or expired tokens older than 14 days
        var staleCutoff = now.AddDays(-14);
        var staleTokens = await db.RefreshTokens
            .Where(r => (r.RevokedAt != null && r.RevokedAt < staleCutoff) || (r.ExpiresAt < staleCutoff))
            .ToListAsync(stoppingToken);

        if (staleTokens.Count > 0)
        {
            db.RefreshTokens.RemoveRange(staleTokens);
        }

        if (expiredOtps.Count > 0 || staleTokens.Count > 0)
        {
            await db.SaveChangesAsync(stoppingToken);
            _logger.LogInformation("Database cleanup executed: removed {OtpCount} expired OTPs and {TokenCount} stale refresh tokens.",
                expiredOtps.Count, staleTokens.Count);
        }
    }
}
