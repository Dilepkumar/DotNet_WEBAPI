using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;
using System.Security.Claims;
using static RoomLedger.Application.Services.AuthService;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    public AuthController(AuthService auth) => _auth = auth;

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        var (ok, msg, devOtp) = await _auth.RegisterAsync(dto);
        return ok ? Ok(new { message = msg, devOtp }) : BadRequest(new { message = msg });
    }

    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp(VerifyOtpDto dto)
    {
        var (ok, msg) = await _auth.VerifyOtpAsync(dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        var (ok, msg, result) = await _auth.LoginAsync(dto);
        return ok ? Ok(result) : Unauthorized(new { message = msg });
    }
    [HttpPost("refresh")]              // [AllowAnonymous]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequestDto dto)
    {
        var (ok, result) = await _auth.RefreshAsync(dto);
        return ok ? Ok(result) : Unauthorized(new { message = "Invalid or expired refresh token — please log in again" });
    }

    [HttpPost("logout")]
    //[Authorize]
    public async Task<IActionResult> Logout()
    {
        var me = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var (ok, msg) = await _auth.LogoutAsync(me);
        return Ok(new { message = msg });
    }
}
