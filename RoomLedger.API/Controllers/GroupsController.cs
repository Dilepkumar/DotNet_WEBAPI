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
        var myGroupIds = await _db.GroupMembers.Where(gm => gm.UserId == Me && gm.Status == MemberStatus.Active).Select(gm => gm.GroupId).ToListAsync();

        var groups = await _db.Groups.Where(g => myGroupIds.Contains(g.Id))
            .Select(g => new
            {
                id = g.Id,
                groupName = g.GroupName,
                address = g.Address,
                monthlyPoolTarget = g.MonthlyPoolTarget,
                inviteCode = g.InviteCode,
                memberCount = _db.GroupMembers.Count(m => m.GroupId == g.Id && m.Status == MemberStatus.Active),
                myRole = _db.GroupMembers.Where(m => m.GroupId == g.Id && m.UserId == Me).Select(m => m.Role.ToString()).FirstOrDefault()
            }).ToListAsync();
        return Ok(groups);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id);
        if (g == null) return NotFound(new { message = "Group not found" });

        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == Me && m.Status == MemberStatus.Active);
        if (!isMember) return Forbid();

        var memberCount = await _db.GroupMembers.CountAsync(m => m.GroupId == id && m.Status == MemberStatus.Active);
        var myMembership = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == Me);

        var memberRows = await _db.GroupMembers.Where(m => m.GroupId == id && m.Status == MemberStatus.Active).ToListAsync();

        var userIds = memberRows.Where(m => m.UserId > 0).Select(m => m.UserId).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u);

        var members = memberRows.Select(m =>
        {
            var u = users.GetValueOrDefault(m.UserId);
            return new
            {
                userId = m.UserId,
                fullName = m.AliasName ?? u?.FullName ?? $"Member #{m.UserId}",
                avatarUrl = u?.AvatarUrl,
                role = m.Role.ToString(),
                status = m.Status.ToString()
            };
        }).ToList();

        return Ok(new
        {
            id = g.Id,
            groupName = g.GroupName,
            address = g.Address,
            monthlyPoolTarget = g.MonthlyPoolTarget,
            inviteCode = g.InviteCode,
            memberCount = memberCount,
            myRole = myMembership?.Role.ToString(),
            members = members
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateGroupDto dto)
    {
        var inviteCode = await GenerateUniqueInviteCodeAsync(dto.GroupName);
        var g = new Group
        {
            GroupName = dto.GroupName,
            Address = dto.Address?.Trim(),
            CreatedByUserId = Me,
            InviteCode = inviteCode,
            MonthlyPoolTarget = dto.MonthlyPoolTarget
        };
        _db.Groups.Add(g);
        await _db.SaveChangesAsync();

        _db.GroupMembers.Add(new GroupMember { GroupId = g.Id, UserId = Me, Role = MemberRole.Admin });
        await _db.SaveChangesAsync();
        return Ok(new { id = g.Id, inviteCode = g.InviteCode, address = g.Address });
    }

    [HttpPost("join")]
    public async Task<IActionResult> Join(JoinGroupDto dto)
    {
        var code = dto.Code.Trim();
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.InviteCode != null && x.InviteCode.ToLower() == code.ToLower());
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

    private async Task<string> GenerateUniqueInviteCodeAsync(string groupName)
    {
        string prefix = "APT";

        if (!string.IsNullOrWhiteSpace(groupName))
        {
            var clean = groupName.Trim();
            var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length > 0 && words[0].Length >= 2 && words[0].All(char.IsLetter))
            {
                var first = words[0].ToUpperInvariant();
                if (first.StartsWith("APT") || first.StartsWith("APART")) prefix = "APT";
                else if (first.StartsWith("FLAT")) prefix = "FLAT";
                else if (first.StartsWith("ROOM")) prefix = "ROOM";
                else prefix = first.Length <= 4 ? first : first[..3];
            }

            var digits = new string(clean.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(digits) && digits.Length is >= 2 and <= 4)
            {
                var candidate = $"{prefix}{digits}";
                if (!await _db.Groups.AnyAsync(g => g.InviteCode == candidate))
                {
                    return candidate;
                }
            }
        }

        for (int i = 0; i < 50; i++)
        {
            var code = $"{prefix}{Random.Shared.Next(100, 999)}";
            if (!await _db.Groups.AnyAsync(g => g.InviteCode == code))
            {
                return code;
            }
        }

        return $"{prefix}{Random.Shared.Next(1000, 9999)}";
    }
}
