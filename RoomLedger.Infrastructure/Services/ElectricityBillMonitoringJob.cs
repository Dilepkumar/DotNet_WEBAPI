using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoomLedger.Application.Services;

namespace RoomLedger.Infrastructure.Services;

/// <summary>
/// Background job that runs twice a day (and polls every 15 minutes for due check times)
/// to fetch electricity bills for accounts where MonitoringStatus == MONITORING and NextCheckAt <= current time.
/// </summary>
public class ElectricityBillMonitoringJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ElectricityBillMonitoringJob> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(15);

    public ElectricityBillMonitoringJob(IServiceProvider serviceProvider, ILogger<ElectricityBillMonitoringJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Electricity Bill Monitoring Background Job started.");

        // Initial brief delay so application boot completes
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var billService = scope.ServiceProvider.GetRequiredService<ElectricityBillService>();
                await billService.ProcessDueAccountsForMonitoringJobAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Exception occurred during ElectricityBillMonitoringJob run.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Electricity Bill Monitoring Background Job stopped.");
    }
}
