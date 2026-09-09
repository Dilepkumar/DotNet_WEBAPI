using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;
using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RoomLedger.Application.Services;

public class AuthService
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;

    public AuthService(IApplicationDbContext db, IPasswordHasher hasher, IJwtService jwt, IEmailService email, IConfiguration config)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _email = email;
        _config = config;
    }
    public record RefreshRequestDto(string RefreshToken);
    public record RefreshResponseDto(string AccessToken, string RefreshToken);
    private static string GenerateRefreshToken()
    => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<(string token, RefreshToken entity)> IssueRefreshTokenAsync(int userId)
    {
        var raw = GenerateRefreshToken();
        var rt = new RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _db.RefreshTokens.Add(rt);
        await _db.SaveChangesAsync();
        return (raw, rt); //raw goes to client; only the hash is stored
    }

    public async Task<(bool ok, string message, object? result)> RegisterAsync(RegisterDto dto)
    {
        var email = dto.Email.Trim().ToLower();
        var phone = "+91" + Regex.Replace(dto.Phone, @"[\s\-+]", "");

        if (await _db.Users.AnyAsync(u => u.Email == email))
            return (false, "Email already registered", null);
        if (await _db.Users.AnyAsync(u => u.Phone == phone))
            return (false, "Phone number already registered", null);

        var user = new User
        {
            FullName = dto.FullName.Trim(),
            Email = email,
            Phone = phone,
            PasswordHash = _hasher.Hash(dto.Password),
            IsEmailVerified = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(dto.RoomInviteCode))
        {
            var code = dto.RoomInviteCode.Trim();
            var group = await _db.Groups.FirstOrDefaultAsync(g => g.InviteCode == code);
            if (group != null)
            {
                _db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = user.Id });
                await _db.SaveChangesAsync();
            }
        }

        var (refreshRaw, _) = await IssueRefreshTokenAsync(user.Id);
        return (true, "Registration successful", new
        {
            token = _jwt.CreateToken(user.Id, user.Email),
            refreshToken = refreshRaw,
            user = new { id = user.Id, fullName = user.FullName, email = user.Email, phone = user.Phone }
        });
    }
    public async Task<(bool ok, string message)> VerifyOtpAsync(VerifyOtpDto dto, OtpPurpose purpose = OtpPurpose.Registration)
    {
        var otp = await _db.OtpCodes.FirstOrDefaultAsync(o =>
            o.Email == dto.Email && o.Code == dto.Code &&
            o.Purpose == purpose && !o.IsUsed &&
            o.ExpiresAt > DateTime.UtcNow);
        if (otp == null) return (false, "Invalid or expired OTP");

        otp.IsUsed = true;

        if (purpose == OtpPurpose.Registration)
        {
            var user = await _db.Users.FirstAsync(u => u.Email == dto.Email);
            user.IsEmailVerified = true;
        }

        await _db.SaveChangesAsync();
        return (true, "OTP verified");
    }

    public async Task<(bool ok, string message, object? result)> LoginAsync(LoginDto dto)
    {
        var id = dto.Identifier.Trim().ToLower();

        // match by email OR phone (handles +91 / bare 10-digit)
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Email == id ||
            u.Phone == id ||
            u.Phone == "+91" + id ||
            u.Phone!.Replace("+91", "") == id);

        if (user == null || !_hasher.Verify(dto.Password, user.PasswordHash))
            return (false, "Invalid email/phone or password", null);
        if (!user.IsEmailVerified)
        {
            user.IsEmailVerified = true;
            await _db.SaveChangesAsync();
        }

        var (refreshRaw, _) = await IssueRefreshTokenAsync(user.Id);
        return (true, "Login successful", new
        {
            token = _jwt.CreateToken(user.Id, user.Email),
            refreshToken = refreshRaw,
            user = new { id = user.Id, fullName = user.FullName, email = user.Email, phone = user.Phone }
        });
    }

    private async Task<string> CreateOtpAsync(string email, OtpPurpose purpose)
    {
        var code = Random.Shared.Next(100000, 999999).ToString();
        _db.OtpCodes.Add(new OtpCode { Email = email, Code = code, Purpose = purpose,
                                       ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await _db.SaveChangesAsync();
        await _email.SendAsync(email, "RoomLedger OTP", $"Your code: {code}");
        return code; // returned for dev testing only (SmtpEmailService logs to console in dev)
    }
    public async Task<(bool ok, object result)> RefreshAsync(RefreshRequestDto dto)
    {
        var incomingHash = HashToken(dto.RefreshToken);

        var stored = await _db.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == incomingHash);

        // ── REUSE DETECTION: token was already rotated/revoked → kill whole family
        if (stored != null && stored.RevokedAt != null)
        {
            var family = await _db.RefreshTokens
                .Where(r => r.UserId == stored.UserId && r.RevokedAt == null).ToListAsync();
            foreach (var r in family) r.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (false, null!);   // client must re-login
        }

        if (stored == null || !stored.IsActive)
            return (false, null!);   // unknown or expired → re-login

        // ── ROTATE: revoke old, link to its replacement, issue new pair
        var (newRefreshRaw, newEntity) = await IssueRefreshTokenAsync(stored.UserId);
        stored.RevokedAt = DateTime.UtcNow;
        stored.ReplacedByHash = newEntity.TokenHash;
        await _db.SaveChangesAsync();

        var user = await _db.Users.FirstAsync(u => u.Id == stored.UserId);
        var jwt = GenerateJwt(user);
        return (true, new RefreshResponseDto(jwt, newRefreshRaw));
    }

    public async Task<(bool ok, string message)> LogoutAsync(int userId)
    {
        var active = await _db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null).ToListAsync();
        foreach (var r in active) r.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, "Logged out — all sessions revoked");
    }
    private string GenerateJwt(User user)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Name, user.FullName)
    };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    public async Task<(bool ok, string message, string? devOtp)> ForgotPasswordAsync(ForgotPasswordDto dto)
    {
        var email = dto.Email.Trim().ToLower();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

        // ⚠️ same response whether or not the account exists (prevents user enumeration)
        if (user == null)
            return (true, "If that email exists, a reset code has been sent", null);

        var devOtp = await CreateOtpAsync(email, OtpPurpose.PasswordReset);   // ← reuse your OTP creator
        return (true, "If that email exists, a reset code has been sent", devOtp);
    }
    public async Task<(bool ok, string message)> ResetPasswordAsync(ResetPasswordDto dto)
    {
        var email = dto.Email.Trim().ToLower();

        var (ok, msg) = await VerifyOtpAsync(new VerifyOtpDto(email, dto.Code), OtpPurpose.PasswordReset);
        if (!ok) return (false, msg);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null) return (false, "Account not found");

        user.PasswordHash = _hasher.Hash(dto.NewPassword);
        await _db.SaveChangesAsync();

        return (true, "Password updated — you can now log in");
    }
}
