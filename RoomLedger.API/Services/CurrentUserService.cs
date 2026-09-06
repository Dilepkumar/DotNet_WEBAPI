using System.Security.Claims;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.API.Services;

public class CurrentUserService : ICurrentUserService
{
    public CurrentUserService(IHttpContextAccessor accessor)
    {
        var id = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        UserId = int.TryParse(id, out var v) ? v : 0;
    }

    public int UserId { get; }
}
