namespace MohamedTransit.API.DTO.MOT.Request;

/// <summary>Payload for drafting, advancing, or blocking a service stage.</summary>
public sealed record UpdateCaseExecutorStageRequest(
    string Action,
    string StageStatus,
    string RiskChannel,
    string? StageNote,
    long? StageId = null);

/// <summary>Multipart payload for attaching a document to a stage.</summary>
public sealed class UploadCaseExecutorStageDocumentRequest
{
    public long? StageId { get; set; }
    public IFormFile File { get; set; } = null!;
    public string DocumentType { get; set; } = string.Empty;
}

/// <summary>Payload for adding a stage discussion message.</summary>
public sealed record CaseExecutorDiscussionRequest(string Message);

/// <summary>Payload for notifying or messaging the importer.</summary>
public sealed record CaseExecutorImporterMessageRequest(string? Subject, string Message);

/// <summary>Payload for escalating an assigned service.</summary>
public sealed record CaseExecutorEscalationRequest(string Reason, string? Priority);
