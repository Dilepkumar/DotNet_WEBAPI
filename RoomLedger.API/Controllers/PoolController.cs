using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.Common.Interfaces;
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
    public async Task<IActionResult> UploadReceipt(int groupId, IFormFile file, [FromQuery] int? expenseId, [FromServices] ICloudStorageService cloud, [FromServices] IApplicationDbContext db)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file provided" });
        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(new { message = "Max file size is 10 MB" });

        var allowed = new[] { "image/jpeg", "image/png", "image/webp", "image/gif", "application/pdf" };
        if (!allowed.Contains(file.ContentType))
            return BadRequest(new { message = "Only JPG, PNG, WebP, GIF or PDF allowed" });

        // Upload to Cloudinary → roomledger/receipts folder
        var url = await cloud.UploadAsync(file, "roomledger/receipts");

        // If expenseId is provided, save URL to the expense record
        if (expenseId.HasValue)
        {
            var expense = await db.PoolExpenses.FindAsync(expenseId.Value);
            if (expense != null)
            {
                expense.ReceiptUrl = url;
                await db.SaveChangesAsync();
            }
        }
        return Ok(new { receiptUrl = url });
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

    [HttpGet("history")]
    public async Task<IActionResult> History(int groupId, [FromQuery] string? period, [FromQuery] string? fromDate, [FromQuery] string? toDate)
        => Ok(await _pool.GetHistoryAsync(groupId, period, fromDate, toDate));

    [HttpPost("reimburse-out-of-pocket")]
    public async Task<IActionResult> ReimburseOutOfPocket(int groupId, [FromBody] ReimburseOutOfPocketDto dto)
    {
        var (ok, msg, amount) = await _pool.ReimburseOutOfPocketAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg, reimbursedAmount = amount }) : BadRequest(new { message = msg });
    }
}

