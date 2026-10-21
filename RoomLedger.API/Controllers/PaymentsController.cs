using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.DTOs;

namespace RoomLedger.API.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(IPaymentService paymentService, ILogger<PaymentsController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    private int Me => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Initiates a new YouPI PG payment session (for Pool, Bills, Electricity, or IOU)
    /// </summary>
    [HttpPost("create-session")]
    [Authorize]
    public async Task<IActionResult> CreateSession([FromBody] CreatePaymentSessionRequestDto dto, CancellationToken ct)
    {
        var (ok, message, session) = await _paymentService.CreatePaymentSessionAsync(Me, dto, ct);
        if (!ok)
        {
            return BadRequest(new { message });
        }

        return Ok(session);
    }

    /// <summary>
    /// Checks the real-time status of a payment by its RoomLedger Order ID
    /// </summary>
    [HttpGet("status/{orderId}")]
    [Authorize]
    public async Task<IActionResult> GetStatus(string orderId, CancellationToken ct)
    {
        var (ok, message, transaction) = await _paymentService.GetStatusAsync(Me, orderId, ct);
        if (!ok)
        {
            return NotFound(new { message });
        }

        return Ok(transaction);
    }

    /// <summary>
    /// Confirms and auto-fulfills a payment directly using the Bank UTR reference from GPay/PhonePe
    /// </summary>
    [HttpPost("confirm")]
    [Authorize]
    public async Task<IActionResult> ConfirmPayment([FromBody] ConfirmPaymentRequestDto dto, CancellationToken ct)
    {
        var (ok, message, transaction) = await _paymentService.ConfirmPaymentAsync(Me, dto, ct);
        if (!ok)
        {
            return BadRequest(new { message });
        }

        return Ok(transaction);
    }

    /// <summary>
    /// Returns all payment claims in a group awaiting Admin verification
    /// </summary>
    [HttpGet("pending-approvals")]
    [Authorize]
    public async Task<IActionResult> GetPendingApprovals([FromQuery] int groupId, CancellationToken ct)
    {
        var list = await _paymentService.GetPendingApprovalsAsync(Me, groupId, ct);
        return Ok(list);
    }

    /// <summary>
    /// Admin verifies and approves/rejects a payment proof claim
    /// </summary>
    [HttpPost("approve")]
    [Authorize]
    public async Task<IActionResult> ApprovePayment([FromBody] ApprovePaymentRequestDto dto, CancellationToken ct)
    {
        var (ok, message, transaction) = await _paymentService.ApprovePaymentAsync(Me, dto, ct);
        if (!ok)
        {
            return BadRequest(new { message });
        }

        return Ok(new { message, transaction });
    }
}
