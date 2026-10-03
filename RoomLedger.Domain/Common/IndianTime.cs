namespace RoomLedger.Domain.Common;

/// <summary>
/// Centralized provider for Indian Standard Time (IST, UTC+05:30).
/// Guarantees consistent date, time, and month calculations across Windows, Linux, and Cloud environments.
/// </summary>
public static class IndianTime
{
    private static readonly TimeZoneInfo IstZone = ResolveIstTimeZone();

    private static TimeZoneInfo ResolveIstTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch
            {
                // Fallback for isolated containers without IANA/Windows tzdata
                return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
            }
        }
    }

    /// <summary>
    /// Current DateTime in Indian Standard Time (IST, UTC+05:30)
    /// </summary>
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstZone);

    /// <summary>
    /// Current DateOnly in Indian Standard Time (IST, UTC+05:30)
    /// </summary>
    public static DateOnly Today => DateOnly.FromDateTime(Now);

    /// <summary>
    /// Current year and month in IST as "yyyy-MM" (e.g. "2026-10")
    /// </summary>
    public static string CurrentMonth => Now.ToString("yyyy-MM");
}
