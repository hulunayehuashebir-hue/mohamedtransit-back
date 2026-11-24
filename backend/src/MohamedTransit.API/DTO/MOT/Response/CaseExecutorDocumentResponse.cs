using MohamedTransit.Domain.Common;

namespace MohamedTransit.API.DTO.MOT.Response;

public sealed class CaseExecutorDocumentResponse
{
    public long Id { get; set; }
    public string Source { get; set; } = "stage";
    public long ServiceId { get; set; }
    public string ServiceNumber { get; set; } = string.Empty;
    public long? StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    public string? Description { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public bool UploadedByCurrentUser { get; set; }
    public DateTime UploadedAt { get; set; }
    public string MimeType { get; set; } = "application/octet-stream";
    public bool IsVerified { get; set; }
}
