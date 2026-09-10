using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups/{groupId:int}/pool")]
[Authorize]
public class PoolController : ControllerBase
{
    private readonly PoolService _pool;
    public PoolController(PoolService pool) => _pool = pool;

    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("balance")]
    public async Task<IActionResult> Balance(int groupId)
    => Ok(await _pool.GetPoolBalanceAsync(groupId, Me));

    [HttpPost("contribute")]
    public async Task<IActionResult> Contribute(int groupId, ContributeDto dto)
    {
        var (ok, msg) = await _pool.ContributeAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("expenses")]
    public async Task<IActionResult> AddExpense(int groupId, PoolExpenseDto dto)
    {
        var (ok, msg) = await _pool.AddExpenseAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("receipt")]
    public async Task<IActionResult> UploadReceipt(int groupId, IFormFile file, [FromServices] IWebHostEnvironment env)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "No file uploaded" });
        if (file.Length > 5 * 1024 * 1024) return BadRequest(new { message = "Max 5 MB" });
        var allowed = new[] { "image/jpeg", "image/png", "image/webp", "image/jpg" };
        if (!allowed.Contains(file.ContentType.ToLower())) return BadRequest(new { message = "Only JPG/PNG/WebP images allowed" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"receipt_{groupId}_{DateTime.UtcNow.Ticks}{ext}";
        var folder = Path.Combine(env.WebRootPath ?? "wwwroot", "receipts");
        Directory.CreateDirectory(folder);

        await using var stream = System.IO.File.Create(Path.Combine(folder, fileName));
        await file.CopyToAsync(stream);

        return Ok(new { receiptUrl = $"/receipts/{fileName}" });
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(int groupId)
        => Ok(await _pool.GetOverviewAsync(groupId));

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(int groupId)
        => Ok(await _pool.GetAnalyticsAsync(groupId));

    [HttpGet("categories")]
    public async Task<IActionResult> Categories()
        => Ok(await _pool.ListCategoriesAsync());

    [HttpPost("seed-categories")]
    public async Task<IActionResult> SeedCategories()
    {
        var (ok, msg) = await _pool.SeedCategoriesAsync();
        return Ok(new { message = msg });
    }

    [HttpPost("expenses/{expenseId:int}/void")]
    public async Task<IActionResult> Void(int groupId, int expenseId, VoidDto dto)
    {
        var (ok, msg) = await _pool.VoidExpenseAsync(groupId, Me, expenseId, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }
    [HttpGet("pending-contributions")]
    public async Task<IActionResult> Pending(int groupId)
    => Ok(await _pool.GetPendingContributionsAsync(groupId, Me));

    [HttpPost("contributions/{contributionId:int}/approve")]
    public async Task<IActionResult> Approve(int groupId, int contributionId, [FromBody] ApproveContributionDto? dto = null)
    {
        var (ok, msg) = await _pool.ApproveContributionAsync(groupId, Me, contributionId);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("contributions/{contributionId:int}/reject")]
    public async Task<IActionResult> Reject(int groupId, int contributionId, RejectContributionDto dto)
    {
        var (ok, msg) = await _pool.RejectContributionAsync(groupId, Me, contributionId, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("shares")]
    public async Task<IActionResult> SetShares(int groupId, SetSharesDto dto)
    {
        var (ok, msg) = await _pool.SetSharesAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPut("target")]
    public async Task<IActionResult> SetTarget(int groupId, SetTargetDto dto)
    {
        var (ok, msg) = await _pool.SetTargetAsync(groupId, Me, dto.MonthlyPoolTarget);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }
}
