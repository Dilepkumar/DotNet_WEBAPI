using Microsoft.AspNetCore.SignalR;
using RoomLedger.API.Hubs;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Domain.Common;

namespace RoomLedger.API.Services;

public class LedgerBroadcastService : ILedgerBroadcastService
{
    private readonly IHubContext<LedgerHub> _hub;

    public LedgerBroadcastService(IHubContext<LedgerHub> hub)
    {
        _hub = hub;
    }

    public async Task BroadcastEventAsync(int groupId, string eventType, object? payload = null)
    {
        var groupName = $"group_{groupId}";
        await _hub.Clients.Group(groupName).SendAsync("LedgerEvent", new
        {
            groupId,
            eventType,
            payload,
            timestamp = IndianTime.Now
        });
    }
}
