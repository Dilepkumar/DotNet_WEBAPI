using Microsoft.Extensions.Logging;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.Infrastructure.Services;

/// <summary>
/// Alias for backward compatibility with UpiPaymentService
/// </summary>
public class YoupiPaymentService : UpiPaymentService
{
    public YoupiPaymentService(IApplicationDbContext db, ILogger<UpiPaymentService> logger, IPushNotificationService push)
        : base(db, logger, push)
    {
    }
}
