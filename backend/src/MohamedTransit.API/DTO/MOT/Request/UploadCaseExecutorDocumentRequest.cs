using System.ComponentModel.DataAnnotations;

using MohamedTransit.Domain.Common;

namespace MohamedTransit.API.DTO.MOT.Request;

public sealed class UploadCaseExecutorDocumentRequest
{
    [Required]
    public long ShipmentId { get; set; }

    public long? ShipmentStageId { get; set; }

    [Required]
    public IFormFile File { get; set; } = null!;

    [Required]
    public DocumentType DocumentType { get; set; }

    public string? Description { get; set; }
}
