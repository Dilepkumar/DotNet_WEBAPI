using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Domain.Common;
using System.Security.Claims;

namespace RoomLedger.API.Infrastructure.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireGroupMemberAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var user = httpContext.User;

        var rawUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(rawUserId) || !int.TryParse(rawUserId, out var userId))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "User is not authenticated" });
            return;
        }

        // Check for groupId in route data (either "groupId" or "id")
        int? groupId = null;
        if (context.RouteData.Values.TryGetValue("groupId", out var gidObj) && gidObj != null && int.TryParse(gidObj.ToString(), out var parsedGid))
        {
            groupId = parsedGid;
        }
        else if (context.RouteData.Values.TryGetValue("id", out var idObj) && idObj != null && int.TryParse(idObj.ToString(), out var parsedId))
        {
            groupId = parsedId;
        }

        if (groupId.HasValue && groupId.Value > 0)
        {
            var db = httpContext.RequestServices.GetRequiredService<IApplicationDbContext>();
            var isMember = await db.GroupMembers.AnyAsync(m =>
                m.GroupId == groupId.Value &&
                m.UserId == userId &&
                m.Status == MemberStatus.Active);

            if (!isMember)
            {
                context.Result = new ObjectResult(new { message = "Forbidden: You are not an active member of this group." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }
        }

        await next();
    }
}
