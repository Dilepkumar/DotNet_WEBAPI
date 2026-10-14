using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Hangfire.SqlServer;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Infrastructure.Configuration;
using RoomLedger.Infrastructure.Persistence;
using RoomLedger.Infrastructure.Services;
using RoomLedger.Infrastructure.Services.Electricity;

namespace RoomLedger.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlServer(config.GetConnectionString("DefaultConnection")));

        services.AddScoped<IApplicationDbContext>(sp =>
            sp.GetRequiredService<ApplicationDbContext>());

        services.Configure<SmtpSettings>(options => config.GetSection("Smtp").Bind(options));

        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IEmailTemplateService, EmailTemplateService>();

        // Cloudinary cloud storage
        services.Configure<CloudinarySettings>(config.GetSection("Cloudinary"));
        services.AddScoped<ICloudStorageService, CloudinaryStorageService>();

        // Web Push Notifications
        services.Configure<WebPushSettings>(config.GetSection("WebPush"));
        services.AddScoped<IPushNotificationService, WebPushNotificationService>();

        // TGSPDCL Electricity Bill Provider
        services.Configure<TgspdclSettings>(config.GetSection("Tgspdcl"));
        services.AddHttpClient<IElectricityBillProvider, TgspdclElectricityBillProvider>();

        // Database Cleanup Service
        services.AddScoped<IDatabaseCleanupService, DatabaseCleanupService>();

        // Hangfire Distributed Background Job Processing
        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(config.GetConnectionString("DefaultConnection"), new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = Math.Max(2, Environment.ProcessorCount);
        });

        return services;
    }
}
