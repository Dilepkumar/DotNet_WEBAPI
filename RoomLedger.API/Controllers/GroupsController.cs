using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    public GroupsController(IApplicationDbContext db) => _db = db;

    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("my")]
    public async Task<IActionResult> My() => Ok(await _db.GroupMembers
        .Where(gm => gm.UserId == Me && gm.Status == MemberStatus.Active)
        .Select(gm => new { gm.GroupId, gm.Role })
        .ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create(CreateGroupDto dto)
    {
        var g = new Group
        {
            GroupName = dto.GroupName,
            CreatedByUserId = Me,
            InviteCode = Random.Shared.Next(100000, 999999).ToString(),
            MonthlyPoolTarget = dto.MonthlyPoolTarget
        };
        _db.Groups.Add(g);
        await _db.SaveChangesAsync();

        _db.GroupMembers.Add(new GroupMember { GroupId = g.Id, UserId = Me, Role = MemberRole.Admin });
        await _db.SaveChangesAsync();
        return Ok(new { id = g.Id, inviteCode = g.InviteCode });
    }

    [HttpPost("join")]
    public async Task<IActionResult> Join(JoinGroupDto dto)
    {
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.InviteCode == dto.Code);
        if (g == null) return BadRequest(new { message = "Invalid code" });

        if (!await _db.GroupMembers.AnyAsync(m => m.GroupId == g.Id && m.UserId == Me))
        {
            _db.GroupMembers.Add(new GroupMember { GroupId = g.Id, UserId = Me });
            await _db.SaveChangesAsync();
        }
        return Ok(new { groupId = g.Id, groupName = g.GroupName });
    }
}
