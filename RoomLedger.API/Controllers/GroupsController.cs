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
    public async Task<IActionResult> My()
    {
        var myGroupIds = await _db.GroupMembers
            .Where(gm => gm.UserId == Me && gm.Status == MemberStatus.Active)
            .Select(gm => gm.GroupId)
            .ToListAsync();

        var groups = await _db.Groups
            .Where(g => myGroupIds.Contains(g.Id))
            .Select(g => new
            {
                id = g.Id,
                groupName = g.GroupName,
                monthlyPoolTarget = g.MonthlyPoolTarget,
                inviteCode = g.InviteCode,
                memberCount = _db.GroupMembers.Count(m => m.GroupId == g.Id && m.Status == MemberStatus.Active),
                myRole = _db.GroupMembers
                    .Where(m => m.GroupId == g.Id && m.UserId == Me)
                    .Select(m => m.Role.ToString())
                    .FirstOrDefault()
            })
            .ToListAsync();

        return Ok(groups);
    }

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
        var code = dto.Code.Trim();
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.InviteCode.ToLower() == code.ToLower());
        if (g == null) return BadRequest(new { message = "Invalid invite code" });

        if (!await _db.GroupMembers.AnyAsync(m => m.GroupId == g.Id && m.UserId == Me))
        {
            _db.GroupMembers.Add(new GroupMember { GroupId = g.Id, UserId = Me, Role = MemberRole.Member });
            await _db.SaveChangesAsync();
        }
        return Ok(new { groupId = g.Id, groupName = g.GroupName });
    }

    [HttpPost("{memberId:int}/role")]
    public async Task<IActionResult> SetRole(int groupId, int memberId, [FromBody] int role) // 0=Member, 1=Admin
    {
        var m = await _db.GroupMembers.FirstOrDefaultAsync(x => x.Id == memberId && x.GroupId == groupId);
        if (m == null) return NotFound();
        if (!await _db.GroupMembers.AnyAsync(x => x.GroupId == groupId && x.UserId == Me
            && x.Role == MemberRole.Admin && x.Status == MemberStatus.Active))
            return BadRequest(new { message = "Only admins can change roles" });
        m.Role = (MemberRole)role;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Role updated" });
    }
}
