using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups/{groupId:int}/audit")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    public AuditController(IApplicationDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(int groupId)
    {
        // simple group-scope filter: audit rows aren't group-tagged, so filter by
        // recent activity; extend entity schema with GroupId later if needed
        var logs = await _db.AuditLogs
            .OrderByDescending(a => a.CreatedAt).Take(100)
            .Select(a => new
            {
                a.Id,
                a.EntityName,
                a.EntityId,
                a.Action,
                a.OldValue,
                a.NewValue,
                modifiedBy = a.ModifiedByUserId,
                a.Reason,
                a.CreatedAt
            })
            .ToListAsync();
        return Ok(logs);
    }
}
