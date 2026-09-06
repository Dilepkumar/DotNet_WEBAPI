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

}
