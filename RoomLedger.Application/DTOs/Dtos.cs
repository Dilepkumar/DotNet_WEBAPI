using System.ComponentModel.DataAnnotations;

namespace RoomLedger.Application.DTOs;
public record RegisterDto(
    [Required, MinLength(2)] string FullName,
    [Required, EmailAddress] string Email,
    [Required, RegularExpression(@"^(\+91[\s-]?)?[6-9]\d{9}$", ErrorMessage = "Invalid Indian mobile number")]
    string Phone,
    [Required, MinLength(8)] string Password,
    string? RoomInviteCode = null);

public record VerifyOtpDto(string Email, string Code);
public record RequestOtpDto(string Email, string? Purpose = null);
public record LoginDto(
    [Required] string Identifier,
    [Required] string Password);
public record CreateGroupDto(string GroupName, decimal MonthlyPoolTarget);
public record JoinGroupDto(string Code);
public record BillDto(string BillName, decimal Amount, DateOnly BillingMonth, int DueDayOfMonth);
public record ContributeDto(
    decimal Amount,
    string? TransactionRef = null,
    string? Message = null,
    List<int>? MemberUserIds = null,
    string? Mode = null);
public record PoolItemDto(int? CategoryId, string ItemName, decimal Amount);
public record PoolExpenseDto(
    string Description, 
    string ExpenseDate, 
    List<PoolItemDto> Items,
    string? Category = null,
    string? ReceiptUrl = null,
    int? PaidByUserId = null,
    string? PayerType = null,
    List<string>? SharedMemberIds = null);
public record SettleUpDto(int PayeeId, decimal Amount, string? TransactionRef);
public record FlexibleSettleDto(int? PayeeId, int? ToUserId, decimal Amount, string? TransactionRef, string? Note);
public record TransferDto(int FromUserId, int ToUserId, decimal Amount);
public record DebtPairDto(int DebtorId, int CreditorId, decimal Amount);
public record GenerateSplitsDto(string BillingMonth);
public record VoidDto(string Reason);
public record EditIouDto(string Description, decimal Amount, string? Reason);
public record EditPoolExpenseDto(string Description, List<PoolItemDto> Items, string? Reason);
public record IouParticipantDto(int UserId, decimal? ShareAmount);   // null = equal share
public record IouExpenseDto(string Description, decimal Amount, List<IouParticipantDto> Participants, string? ExpenseDate);
public record FlexibleIouExpenseDto(string Description, decimal Amount, List<IouParticipantDto>? Participants, List<string>? SharedWith, string? ExpenseDate);
public record IouItemInputDto(string ItemName, decimal Amount, List<int> EaterUserIds);
public record IouItemizedDto(string Description, decimal Amount, string? ExpenseDate, List<IouItemInputDto> Items);
public record DashboardDto(
    decimal PoolBalance,
    decimal UnpaidBillTotal,
    decimal OutstandingIouDebt,
    decimal OutstandingIouCredit,
    int UnreadNotifications,
    List<UpcomingBillDto> UpcomingBills);

public record UpcomingBillDto(string BillName, decimal ShareAmount, int DueDay, bool IsPaid);
public record NotificationDto(int Id, string Title, string Message, string Type, bool IsRead, DateTime CreatedAt);
public record ApproveContributionDto(string? Note);
public record RejectContributionDto(string Reason);
public record SetSharesDto(List<MemberShareDto> Shares);
public record MemberShareDto(int? UserId, string? AliasName, decimal MonthlyShare);
public record SetTargetDto(decimal MonthlyPoolTarget);
public record ForgotPasswordDto([Required, EmailAddress] string Email);
public record ResetPasswordDto(
    [Required, EmailAddress] string Email,
    [Required] string Code,
    [Required, MinLength(8)] string NewPassword);
public record UpdateProfileDto(
    [Required, MinLength(2)] string FullName,
    DateTime? DateOfBirth,
    string? Gender,
    string? Phone = null);
public record ChangePasswordDto(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);

