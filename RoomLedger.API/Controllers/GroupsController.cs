using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;

using RoomLedger.Application.Services;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly IouService _iou;

    public GroupsController(IApplicationDbContext db, IouService iou)
    {
        _db = db;
        _iou = iou;
    }

    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("my")]
    public async Task<IActionResult> My([FromQuery] bool includeInactive = false)
    {
        var myGroupIds = await _db.GroupMembers.Where(gm => gm.UserId == Me && gm.Status == MemberStatus.Active).Select(gm => gm.GroupId).ToListAsync();

        var query = _db.Groups.Where(g => myGroupIds.Contains(g.Id));
        if (!includeInactive)
        {
            query = query.Where(g => g.IsActive);
        }

        var groups = await query
            .Select(g => new
            {
                id = g.Id,
                groupName = g.GroupName,
                address = g.Address,
                monthlyPoolTarget = g.MonthlyPoolTarget,
                inviteCode = g.InviteCode,
                isActive = g.IsActive,
                inactivatedAt = g.InactivatedAt,
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
                upiId = u?.UpiId,
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
            isActive = g.IsActive,
            inactivatedAt = g.InactivatedAt,
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

    [HttpDelete("{groupId:int}/members/{memberUserId:int}")]
    [HttpPost("{groupId:int}/members/{memberUserId:int}/remove")]
    public async Task<IActionResult> RemoveMember(int groupId, int memberUserId)
    {
        // 1. Caller must be an active admin in this group
        var callerMembership = await _db.GroupMembers.FirstOrDefaultAsync(m =>
            m.GroupId == groupId && m.UserId == Me && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);
        if (callerMembership == null)
            return Forbid();

        // 2. Admin cannot remove themselves
        if (memberUserId == Me)
            return BadRequest(new { message = "You cannot remove yourself as admin. Another admin must perform this, or transfer admin privileges first." });

        // 3. Target must be an active member of this group
        var targetMember = await _db.GroupMembers.FirstOrDefaultAsync(m =>
            m.GroupId == groupId && m.UserId == memberUserId && m.Status == MemberStatus.Active);
        if (targetMember == null)
            return NotFound(new { message = "Member not found or already inactive in this group." });

        // 4. Financial Safety Check 1: Outstanding IOUs
        var debts = await _iou.GetDebtMatrixAsync(groupId);
        var owes = debts.Where(d => d.DebtorId == memberUserId && d.Amount > 0.01m).Sum(d => d.Amount);
        var owed = debts.Where(d => d.CreditorId == memberUserId && d.Amount > 0.01m).Sum(d => d.Amount);
        if (owes > 0 || owed > 0)
        {
            var msg = owes > 0 && owed > 0
                ? $"Cannot remove member: User owes ₹{owes:F2} and is owed ₹{owed:F2} in group IOUs. Settle all debts first."
                : owes > 0
                    ? $"Cannot remove member: User owes ₹{owes:F2} to other flatmates in group IOUs. Settle before removing."
                    : $"Cannot remove member: Flatmates owe this user ₹{owed:F2} in group IOUs. Settle before removing.";
            return BadRequest(new { message = msg });
        }

        // 5. Financial Safety Check 2: Unpaid Recurring Bills
        var unpaidBillsCount = await _db.BillSplits.CountAsync(s => s.GroupId == groupId && s.UserId == memberUserId && !s.IsPaid);
        if (unpaidBillsCount > 0)
        {
            return BadRequest(new { message = $"Cannot remove member: User has {unpaidBillsCount} unpaid recurring bill split(s) in this group. Mark them paid or resolve them first." });
        }

        // 6. Financial Safety Check 3: Unreimbursed Pool Expenses
        var unreimbursedExpenses = await _db.PoolExpenses.CountAsync(e => e.GroupId == groupId && e.PaidByUserId == memberUserId && !e.IsReimbursed && !e.IsVoided);
        if (unreimbursedExpenses > 0)
        {
            return BadRequest(new { message = $"Cannot remove member: User has {unreimbursedExpenses} unreimbursed out-of-pocket pool expense(s). Please reimburse from pool first." });
        }

        // Safe to remove
        targetMember.Status = MemberStatus.Removed;

        var grp = await _db.Groups.FindAsync(groupId);
        var targetUser = await _db.Users.FindAsync(memberUserId);

        _db.Notifications.Add(new Notification
        {
            UserId = memberUserId,
            Title = "Removed from Flat",
            Message = $"You have been removed from {grp?.GroupName ?? "the group"} by the administrator.",
            Type = "group_removed",
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new { message = $"{targetUser?.FullName ?? "Member"} has been safely removed from the group." });
    }

    [HttpPost("{id:int}/inactivate")]
    public async Task<IActionResult> InactivateGroup(int id)
    {
        // 1. Caller must be an active admin in this group
        var callerMembership = await _db.GroupMembers.FirstOrDefaultAsync(m =>
            m.GroupId == id && m.UserId == Me && m.Role == MemberRole.Admin && m.Status == MemberStatus.Active);
        if (callerMembership == null)
            return Forbid();

        var g = await _db.Groups.FindAsync(id);
        if (g == null) return NotFound(new { message = "Flat not found" });

        if (!g.IsActive)
            return BadRequest(new { message = "This flat is already inactive / archived." });

        // 2. Financial Safety Check 1: Settle all IOUs
        var debts = await _iou.GetDebtMatrixAsync(id);
        var outstandingDebts = debts.Where(d => d.Amount > 0.01m).ToList();
        if (outstandingDebts.Count > 0)
        {
            var totalOwed = outstandingDebts.Sum(d => d.Amount);
            return BadRequest(new { message = $"Cannot inactivate flat: There are {outstandingDebts.Count} unsettled IOUs totaling ₹{totalOwed:F2} between roommates. Settle all debts before closing the flat." });
        }

        // 3. Financial Safety Check 2: Unpaid Recurring Bills
        var unpaidBillsCount = await _db.BillSplits.CountAsync(s => s.GroupId == id && !s.IsPaid);
        if (unpaidBillsCount > 0)
        {
            return BadRequest(new { message = $"Cannot inactivate flat: There are {unpaidBillsCount} unpaid recurring bill split(s) in this group. Mark them paid or resolve them first." });
        }

        // 4. Financial Safety Check 3: Unreimbursed Pool Expenses
        var unreimbursedExpenses = await _db.PoolExpenses.CountAsync(e => e.GroupId == id && !e.IsReimbursed && !e.IsVoided);
        if (unreimbursedExpenses > 0)
        {
            return BadRequest(new { message = $"Cannot inactivate flat: There are {unreimbursedExpenses} unreimbursed out-of-pocket pool expense(s). Please reimburse them from pool first." });
        }

        // Safe to inactivate
        g.IsActive = false;
        g.InactivatedAt = DateTime.UtcNow;
        g.InactivatedByUserId = Me;

        // Broadcast notifications to all active members of the flat
        var activeMemberUserIds = await _db.GroupMembers
            .Where(m => m.GroupId == id && m.Status == MemberStatus.Active && !(m.IsAlias ?? false) && m.UserId > 0)
            .Select(m => m.UserId)
            .ToListAsync();

        foreach (var uid in activeMemberUserIds)
        {
            _db.Notifications.Add(new Notification
            {
                UserId = uid,
                Title = "Flat Inactivated",
                Message = $"Flat '{g.GroupName}' has been deactivated and archived by the administrator.",
                Type = "group_inactivated",
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        return Ok(new { message = $"Flat '{g.GroupName}' has been successfully deactivated and archived." });
    }

    [HttpPost("{id:int}/reactivate")]
    public async Task<IActionResult> ReactivateGroup(int id)
    {
        var callerMembership = await _db.GroupMembers.FirstOrDefaultAsync(m =>
            m.GroupId == id && m.UserId == Me && m.Role == MemberRole.Admin);
        if (callerMembership == null)
            return Forbid();

        var g = await _db.Groups.FindAsync(id);
        if (g == null) return NotFound(new { message = "Flat not found" });

        if (g.IsActive)
            return BadRequest(new { message = "This flat is already active." });

        g.IsActive = true;
        g.InactivatedAt = null;
        g.InactivatedByUserId = null;

        await _db.SaveChangesAsync();

        return Ok(new { message = $"Flat '{g.GroupName}' has been successfully reactivated." });
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
