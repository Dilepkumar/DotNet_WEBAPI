namespace RoomLedger.Domain.Entities;

public class EmailTemplate : BaseEntity
{
    public string TemplateKey { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string HtmlBody { get; set; } = default!;
    public string Status { get; set; } = "Active";
    public bool IsActive { get; set; } = true;
    public DateTime? UpdatedAt { get; set; }
}
