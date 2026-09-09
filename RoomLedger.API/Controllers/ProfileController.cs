using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
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

    public ProfileController(IApplicationDbContext db, IPasswordHasher hasher, IWebHostEnvironment env)
    { _db = db; _hasher = hasher; _env = env; }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var u = await _db.Users.FindAsync(UserId);
        if (u == null) return NotFound();
        return Ok(new
        {
            id = u.Id,
            fullName = u.FullName,
            email = u.Email,
            phone = u.Phone,
            dateOfBirth = u.DateOfBirth,
            gender = u.Gender?.ToString(),
            avatarUrl = u.AvatarUrl
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