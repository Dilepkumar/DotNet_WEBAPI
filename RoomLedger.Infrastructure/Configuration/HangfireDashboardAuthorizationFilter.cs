using Hangfire.Dashboard;

namespace RoomLedger.Infrastructure.Configuration;

public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        // Allow dashboard access
        return true;
    }
}
