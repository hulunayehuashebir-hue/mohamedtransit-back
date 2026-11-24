using System.ComponentModel.DataAnnotations;

namespace MohamedTransit.API.DTO.MOT.Request;

public sealed record CaseExecutorDocumentReference
{
    [Range(1, long.MaxValue)]
    public long DocumentId { get; init; }

    [Required]
    public string Source { get; init; } = string.Empty;
}
