using Microsoft.Extensions.DependencyInjection;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.Services;

namespace RoomLedger.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<IouService>();
        services.AddScoped<BillsService>();
        services.AddScoped<PoolService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<NotificationService>();
        return services;
    }
}
