using System.ComponentModel.DataAnnotations;

namespace MohamedTransit.API.DTO.MOT.Request;

public sealed record DataEncoderStageDocumentUploadRequest
{
    [Range(1, long.MaxValue)]
    public long ServiceId { get; init; }

    [Range(1, long.MaxValue)]
    public long StageId { get; init; }

    [Required]
    public IFormFile File { get; init; } = null!;

    [Required]
    public string DocumentType { get; init; } = string.Empty;
}

public sealed record DataEncoderStageDiscussionRequest
{
    [Required, MaxLength(4000)]
    public string Message { get; init; } = string.Empty;
}

public sealed record DataEncoderImporterMessageRequest
{
    [MaxLength(200)]
    public string? Subject { get; init; }

    [Required, MaxLength(4000)]
    public string Message { get; init; } = string.Empty;
}
