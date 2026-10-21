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
    private readonly IYoupiPaymentService _paymentService;
    private readonly ILogger<PaymentsController> _logger;
    private readonly IWebHostEnvironment _env;

    public PaymentsController(IYoupiPaymentService paymentService, ILogger<PaymentsController> logger, IWebHostEnvironment env)
    {
        _paymentService = paymentService;
        _logger = logger;
        _env = env;
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

    /// <summary>
    /// Authoritative webhook receiver for YouPI-PG payment observer events.
    /// Verified using HMAC-SHA256 signature in X-Youpi-Signature header.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }

        var signature = Request.Headers["X-Youpi-Signature"].FirstOrDefault() 
                     ?? Request.Headers["X-Gateway-Signature"].FirstOrDefault()
                     ?? Request.Headers["X-Signature"].FirstOrDefault()
                     ?? Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                     ?? "";

        _logger.LogInformation("Incoming YouPI PG Webhook received. Body Length: {Length}", rawBody.Length);

        var (ok, message) = await _paymentService.ProcessWebhookAsync(rawBody, signature, ct);
        if (!ok)
        {
            _logger.LogWarning("Webhook rejection: {Message}", message);
            return BadRequest(new { message });
        }

        return Ok(new { message = "Webhook processed successfully" });
    }

    /// <summary>
    /// Simulates a real banking/YouPI webhook callback event (useful for localhost testing where external observer cannot reach port 5056)
    /// </summary>
    [HttpPost("simulate-webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> SimulateWebhook([FromBody] SimulateWebhookRequestDto dto, CancellationToken ct)
    {
        if (!_env.IsDevelopment())
        {
            return NotFound(new { message = "Payment simulation endpoint is only accessible in development environment." });
        }

        if (string.IsNullOrWhiteSpace(dto.OrderId))
            return BadRequest(new { message = "OrderId is required" });

        var (ok, message, transaction) = await _paymentService.SimulateWebhookAsync(dto.OrderId, dto.Utr, ct);
        if (!ok)
        {
            return BadRequest(new { message });
        }

        return Ok(new { message, transaction });
    }
}
