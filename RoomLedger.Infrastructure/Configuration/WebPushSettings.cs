namespace RoomLedger.Infrastructure.Configuration;

public class WebPushSettings
{
    public string PublicKey { get; set; } = default!;
    public string PrivateKey { get; set; } = default!;
    public string Subject { get; set; } = default!;
}
