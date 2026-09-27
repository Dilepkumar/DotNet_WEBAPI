namespace RoomLedger.Application.Common.Interfaces;

public interface IPushNotificationService
{
    Task SendPushNotificationAsync(int userId, string title, string message, string? url = null);
    Task SendPushToUsersAsync(IEnumerable<int> userIds, string title, string message, string? url = null);
}
