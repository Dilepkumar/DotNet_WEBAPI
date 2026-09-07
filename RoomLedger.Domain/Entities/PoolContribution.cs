using RoomLedger.Domain.Common;
using RoomLedger.Domain.Entities;
using System.ComponentModel.DataAnnotations.Schema;

public class PoolContribution : BaseEntity
{
    public int GroupId { get; set; }
    public int UserId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly ContributedOn { get; set; }
    public string? PeriodMonth { get; set; }
    public string? TransactionRef { get; set; }

    [ForeignKey("UserId")]
    public User User { get; set; } = default!;
    public ContributionStatus Status { get; set; } = ContributionStatus.Pending;
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectReason { get; set; }
}
