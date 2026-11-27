using System.ComponentModel.DataAnnotations;

using MohamedTransit.Domain.Common;

namespace MohamedTransit.API.DTO.Document.Request;

public class UploadDocumentRequest
{
    public long ServiceId { get; set; }

    public long ShipmentId { get; set; }

    public long? StageId { get; set; }
    public long? WorkflowStageId { get; set; }
    public long? ShipmentStageId { get; set; }

    [Required]
    public IFormFile File { get; set; } = null!; // null-forgiving because it's required
    public string? FilePath { get; set; }

    [Required]
    public string DocumentType { get; set; } = string.Empty;

    public string? Description { get; set; }

    public long EffectiveServiceId => ServiceId > 0 ? ServiceId : ShipmentId;
    public long? EffectiveStageId => WorkflowStageId ?? StageId ?? ShipmentStageId;
}
