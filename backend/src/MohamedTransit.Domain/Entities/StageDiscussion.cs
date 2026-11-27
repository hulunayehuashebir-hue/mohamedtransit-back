using MohamedTransit.Domain.Common;

namespace MohamedTransit.Domain.Entities;

public class StageDiscussion
{
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public long StageId { get; set; }
    public long AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorRole { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public RecordStatus RecordStatus { get; set; } = RecordStatus.Active;
}
