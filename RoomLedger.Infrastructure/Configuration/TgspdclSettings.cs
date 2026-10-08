namespace RoomLedger.Infrastructure.Configuration;

public class TgspdclSettings
{
    public string BaseUrl { get; set; } = "https://www.tgsouthernpower.org";
    public string ApiKey { get; set; } = "TGSWpDcL2025XkNpMqRvFzAcYeUhBsJ3";
    public int TimeoutSeconds { get; set; } = 15;
}
