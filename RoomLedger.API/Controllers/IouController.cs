using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups/{groupId:int}/iou")]
[Authorize]
public class IouController : ControllerBase
{
    private readonly IouService _iou;
    public IouController(IouService iou) => _iou = iou;

    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("expenses")]
    public async Task<IActionResult> Add(int groupId, IouExpenseDto dto)
    {
        var (ok, msg) = await _iou.AddExpenseAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpGet("debts")]                 // raw who-owes-who (debt matrix view)
    public async Task<IActionResult> Debts(int groupId)
        => Ok(await _iou.GetDebtMatrixAsync(groupId));

    [HttpGet("simplified")]            // minimized transfer plan (smart netting)
    public async Task<IActionResult> Simplified(int groupId)
        => Ok(await _iou.GetSimplifiedDebtsAsync(groupId));

    [HttpGet("my-balance")]
    public async Task<IActionResult> MyBalance(int groupId)
        => Ok(await _iou.GetMyBalanceAsync(groupId, Me));

    [HttpPost("settle")]
    public async Task<IActionResult> Settle(int groupId, SettleUpDto dto)
    {
        var (ok, msg) = await _iou.SettleUpAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPut("expenses/{expenseId:int}")]
    public async Task<IActionResult> Edit(int groupId, int expenseId, EditIouDto dto)
    {
        var (ok, msg) = await _iou.EditExpenseAsync(groupId, Me, expenseId, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("expenses/{expenseId:int}/void")]
    public async Task<IActionResult> Void(int groupId, int expenseId, VoidDto dto)
    {
        var (ok, msg) = await _iou.VoidExpenseAsync(groupId, Me, expenseId, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpGet("expenses")]
    public async Task<IActionResult> Expenses(int groupId)
     => Ok(await _iou.GetExpensesAsync(groupId));

    [HttpPost("expenses/itemized")]
    public async Task<IActionResult> AddItemized(int groupId, IouItemizedDto dto)
    {
        var (ok, msg) = await _iou.AddItemizedExpenseAsync(groupId, Me, dto);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

}
