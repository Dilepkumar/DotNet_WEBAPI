namespace RoomLedger.Infrastructure.Configuration;

public class YoupiSettings
{
    public string BaseUrl { get; set; } = "http://localhost:6100";
    public string CheckoutUrl { get; set; } = "http://localhost:7000";
    public string MerchantId { get; set; } = "00000000-0000-0000-0000-000000000001";
    public string WebhookSecret { get; set; } = "youpi_webhook_secret_dev_32char";
    public int TimeoutSeconds { get; set; } = 15;
}
