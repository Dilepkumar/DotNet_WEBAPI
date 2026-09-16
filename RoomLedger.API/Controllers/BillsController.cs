using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/groups/{groupId:int}/bills")]
[Authorize]
public class BillsController : ControllerBase
{
    private readonly BillsService _bills;
    public BillsController(BillsService bills) => _bills = bills;

    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public async Task<IActionResult> Create(int groupId, BillDto dto)
    {
        var (ok, msg, result) = await _bills.CreateAsync(groupId, Me, dto);
        return ok ? Ok(result) : BadRequest(new { message = msg });
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate(int groupId, GenerateSplitsDto dto)
    {
        var (ok, msg) = await _bills.GenerateSplitsAsync(groupId, Me, dto.BillingMonth);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpGet]
    public async Task<IActionResult> GetBills(int groupId, [FromQuery] string? month)
    {
        var m = string.IsNullOrWhiteSpace(month) ? DateTime.UtcNow.ToString("yyyy-MM") : month;
        return Ok(await _bills.GetMonthAsync(groupId, m));
    }

    [HttpGet("{billingMonth}")]        // e.g. GET api/groups/1/bills/2026-09
    public async Task<IActionResult> Month(int groupId, string billingMonth)
        => Ok(await _bills.GetMonthAsync(groupId, billingMonth));

    [HttpPost("generate-next-month")]
    public async Task<IActionResult> GenerateNextMonth(int groupId)
    {
        var nextMonth = DateTime.UtcNow.AddMonths(1).ToString("yyyy-MM");
        var (ok, msg) = await _bills.GenerateSplitsAsync(groupId, Me, nextMonth);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("splits/{splitId:int}/toggle-paid")]
    public async Task<IActionResult> Toggle(int groupId, int splitId)
    {
        var (ok, msg) = await _bills.TogglePaidAsync(groupId, Me, splitId);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPut("splits/{splitId:int}/mark-paid")]
    [HttpPost("splits/{splitId:int}/mark-paid")]
    public async Task<IActionResult> MarkPaid(int groupId, int splitId)
    {
        var (ok, msg) = await _bills.TogglePaidAsync(groupId, Me, splitId);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpPost("{billId:int}/remind")]
    public async Task<IActionResult> Remind(int groupId, int billId)
    {
        var (ok, msg, count) = await _bills.RemindPendingAsync(groupId, Me, billId);
        return ok ? Ok(new { message = msg, count }) : BadRequest(new { message = msg });
    }

    [HttpDelete("{billId:int}")]
    public async Task<IActionResult> Deactivate(int groupId, int billId)
    {
        var (ok, msg) = await _bills.DeactivateAsync(groupId, Me, billId);
        return ok ? Ok(new { message = msg }) : BadRequest(new { message = msg });
    }

    [HttpGet("trend")]
    public async Task<IActionResult> Trend(int groupId)
        => Ok(await _bills.GetTrendAsync(groupId));
}
