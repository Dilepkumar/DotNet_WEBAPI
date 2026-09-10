using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;
using System.Security.Claims;

namespace RoomLedger.API.Controllers;

[Authorize]
[ApiController]
[Route("api/profile")]
public class ProfileController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;      
    private readonly IWebHostEnvironment _env;
    private readonly IouService _iouService;

    public ProfileController(IApplicationDbContext db, IPasswordHasher hasher, IWebHostEnvironment env, IouService iouService)
    { 
        _db = db; 
        _hasher = hasher; 
        _env = env; 
        _iouService = iouService;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? groupId = null)
    {
        var u = await _db.Users.FindAsync(UserId);
        if (u == null) return NotFound();

        // Active group membership: use query groupId if specified, or first active membership
        GroupMember? membership = null;
        if (groupId.HasValue)
        {
            membership = await _db.GroupMembers
                .FirstOrDefaultAsync(m => m.GroupId == groupId.Value && m.UserId == UserId && m.Status == MemberStatus.Active && (m.IsAlias == null || m.IsAlias == false));
        }

        if (membership == null)
        {
            membership = await _db.GroupMembers
                .Where(m => m.UserId == UserId && m.Status == MemberStatus.Active && (m.IsAlias == null || m.IsAlias == false))
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync();
        }

        string roomName = "No Flat Joined";
        string roomAddress = "Create or join a flat";
        string roomRole = "Flat Member";
        int memberCount = 1;
        decimal owedToYou = 0;
        string? inviteCode = null;
        int? activeGroupId = null;

        if (membership != null)
        {
            activeGroupId = membership.GroupId;
            roomRole = membership.Role == MemberRole.Admin ? "Room Admin" : "Flat Member";

            var grp = await _db.Groups.FindAsync(membership.GroupId);
            if (grp != null)
            {
                roomName = grp.GroupName;
                roomAddress = $"{grp.GroupName} Flat";
                inviteCode = grp.InviteCode;
            }

            memberCount = await _db.GroupMembers
                .CountAsync(m => m.GroupId == membership.GroupId && m.Status == MemberStatus.Active);

            try
            {
                var debts = await _iouService.GetDebtMatrixAsync(membership.GroupId);
                owedToYou = debts.Where(d => d.CreditorId == UserId).Sum(d => d.Amount);
            }
            catch
            {
                owedToYou = 0;
            }
        }

        var userPrefix = !string.IsNullOrEmpty(u.Email) 
            ? u.Email.Split('@')[0] 
            : u.FullName.ToLower().Replace(" ", "");
        var upiId = $"{userPrefix}@okhdfcbank";

        var nameParts = u.FullName.Trim().Split(' ');
        var nickname = nameParts.Length > 1 
            ? string.Concat(nameParts.Select(p => p[0])).ToUpper() 
            : u.FullName;

        return Ok(new
        {
            id = u.Id,
            fullName = u.FullName,
            nickname = nickname,
            email = u.Email,
            phone = u.Phone ?? "",
            dateOfBirth = u.DateOfBirth,
            gender = u.Gender?.ToString(),
            avatarUrl = u.AvatarUrl,
            upiId = upiId,
            roomName = roomName,
            roomAddress = roomAddress,
            roomRole = roomRole,
            memberCount = memberCount,
            owedToYou = owedToYou,
            inviteCode = inviteCode,
            groupId = activeGroupId
        });
    }

    [HttpPut]
    public async Task<IActionResult> Update(UpdateProfileDto dto)
    {
        var u = await _db.Users.FindAsync(UserId);
        if (u == null) return NotFound();

        u.FullName = dto.FullName.Trim();
        u.DateOfBirth = dto.DateOfBirth;
        u.Gender = ParseGender(dto.Gender);
        if (!string.IsNullOrWhiteSpace(dto.Phone))
            u.Phone = dto.Phone.Trim();

        await _db.SaveChangesAsync();
        return Ok(new { message = "Profile updated" });
    }

    [HttpPost("avatar")]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "No file" });
        if (file.Length > 2 * 1024 * 1024) return BadRequest(new { message = "Max 2 MB" });
        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowed.Contains(file.ContentType)) return BadRequest(new { message = "Only JPG/PNG/WebP" });

        var u = await _db.Users.FindAsync(UserId)!;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"avatar_{UserId}_{DateTime.UtcNow.Ticks}{ext}";
        var folder = Path.Combine(_env.WebRootPath ?? "wwwroot", "avatars");
        Directory.CreateDirectory(folder);

        // delete old file
        if (!string.IsNullOrEmpty(u!.AvatarUrl))
        {
            var old = Path.Combine(_env.WebRootPath ?? "wwwroot", u.AvatarUrl.TrimStart('/'));
            if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
        }

        await using var stream = System.IO.File.Create(Path.Combine(folder, fileName));
        await file.CopyToAsync(stream);

        u.AvatarUrl = $"/avatars/{fileName}";
        await _db.SaveChangesAsync();
        return Ok(new { avatarUrl = u.AvatarUrl });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        var u = await _db.Users.FindAsync(UserId);
        if (u == null) return NotFound();
        if (!_hasher.Verify(dto.CurrentPassword, u.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect" });
        if (dto.CurrentPassword == dto.NewPassword)
            return BadRequest(new { message = "New password must be different" });

        u.PasswordHash = _hasher.Hash(dto.NewPassword);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Password changed successfully" });
    }
    private static Gender? ParseGender(string? g) => g?.ToLower() switch
    {
        "male" => Gender.Male,
        "female" => Gender.Female,
        "other" => Gender.Other,
        _ => null
    };
}