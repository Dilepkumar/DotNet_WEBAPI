using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace RoomLedger.API.Hubs;

[Authorize]
public class LedgerHub : Hub
{
    private string? UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    public async Task JoinGroup(int groupId)
    {
        var groupName = $"group_{groupId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task LeaveGroup(int groupId)
    {
        var groupName = $"group_{groupId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task NotifyGroup(int groupId, string eventType, object payload)
    {
        var groupName = $"group_{groupId}";
        await Clients.OthersInGroup(groupName).SendAsync("LedgerEvent", new
        {
            groupId,
            eventType,
            payload,
            senderId = UserId,
            timestamp = DateTime.UtcNow
        });
    }
}
