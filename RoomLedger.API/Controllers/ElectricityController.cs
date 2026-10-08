using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.DTOs;
using RoomLedger.Application.Services;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/electricity")]
[Authorize]
public class ElectricityController : ControllerBase
{
    private readonly ElectricityBillService _electricityService;
    public ElectricityController(ElectricityBillService electricityService)
    {
        _electricityService = electricityService;
    }
    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("billers")]
    public async Task<IActionResult> GetBillers()
    {
        var billers = await _electricityService.GetBillersAsync();
        return Ok(billers);
    }

    [HttpGet("billers/{billerId}")]
    public async Task<IActionResult> GetBillerDetails(string billerId)
    {
        var details = await _electricityService.GetBillerDetailsAsync(billerId);
        if (details == null)
            return NotFound(new { message = $"Biller '{billerId}' not found" });

        return Ok(details);
    }

    /// <summary>
    /// Direct TGSPDCL bill enquiry for testing and verifying consumer numbers in Swagger/browser
    /// </summary>
    [HttpGet("test-enquiry")]
    [AllowAnonymous]
    public async Task<IActionResult> TestEnquiry([FromQuery] string uscno)
    {
        if (string.IsNullOrWhiteSpace(uscno))
            return BadRequest(new { message = "Unique Service Number (uscno) is required. Example: ?uscno=100293847" });

        var bill = await _electricityService.FetchBillDirectAsync(uscno);
        return Ok(bill);
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromBody] CreateElectricityAccountDto dto)
    {
        var (ok, message, account) = await _electricityService.CreateAccountAsync(Me, dto);
        if (!ok)
            return BadRequest(new { message });

        return CreatedAtAction(nameof(GetAccount), new { id = account!.Id }, account);
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts([FromQuery] int groupId)
    {
        if (groupId <= 0)
            return BadRequest(new { message = "GroupId is required" });

        var accounts = await _electricityService.GetAccountsByGroupAsync(groupId);
        return Ok(accounts);
    }

    [HttpGet("accounts/{id:int}")]
    public async Task<IActionResult> GetAccount(int id)
    {
        var account = await _electricityService.GetAccountByIdAsync(id);
        if (account == null)
            return NotFound(new { message = "Electricity account not found" });

        return Ok(account);
    }

    [HttpPut("accounts/{id:int}")]
    public async Task<IActionResult> UpdateAccount(int id, [FromBody] UpdateElectricityAccountDto dto)
    {
        var (ok, message) = await _electricityService.UpdateAccountAsync(id, Me, dto);
        if (!ok)
            return BadRequest(new { message });

        return Ok(new { message });
    }

    // ───────────────────── MANUAL CHECK (2/DAY LIMIT) ─────────────────────

    [HttpPost("accounts/{id:int}/check")]
    public async Task<IActionResult> CheckBill(int id)
    {
        var (ok, message, account, bill) = await _electricityService.CheckBillManualAsync(id, Me);

        if (!ok)
        {
            // If manual limit reached, return 429 Too Many Requests or 400 with detail
            return StatusCode(429, new
            {
                message,
                account,
                bill,
                limitReached = account?.RemainingManualChecksToday == 0
            });
        }

        return Ok(new
        {
            message,
            account,
            bill
        });
    }

    // ───────────────────── BILL, MONITORING & SPLITS ─────────────────────

    [HttpGet("accounts/{id:int}/bill")]
    public async Task<IActionResult> GetLatestBill(int id)
    {
        var bill = await _electricityService.GetLatestBillAsync(id);
        if (bill == null)
            return NotFound(new { message = "No bill found for this electricity account" });

        return Ok(bill);
    }

    [HttpGet("accounts/{id:int}/monitoring")]
    public async Task<IActionResult> GetMonitoringStatus(int id)
    {
        var status = await _electricityService.GetMonitoringStatusAsync(id);
        if (status == null)
            return NotFound(new { message = "Electricity account not found" });

        return Ok(status);
    }

    [HttpGet("accounts/{id:int}/splits")]
    public async Task<IActionResult> GetSplits(int id, [FromQuery] int? billId)
    {
        var splits = await _electricityService.GetSplitsAsync(id, billId);
        if (splits == null)
            return NotFound(new { message = "No bill or splits found for this electricity account" });

        return Ok(splits);
    }
}
