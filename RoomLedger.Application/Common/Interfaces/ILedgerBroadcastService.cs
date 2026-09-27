namespace RoomLedger.Application.Common.Interfaces;

public interface ILedgerBroadcastService
{
    Task BroadcastEventAsync(int groupId, string eventType, object? payload = null);
}
