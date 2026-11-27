namespace MohamedTransit.API.DTO.MOT.Response;

/// <summary>Filters and paging options for the assigned case-executor service list.</summary>
public sealed class CaseExecutorServiceFilter
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? Mode { get; set; }
    public string? RiskChannel { get; set; }
    public string SortBy { get; set; } = "updatedAt";
    public string SortOrder { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>One service in the case-executor assigned-service list.</summary>
public sealed record CaseExecutorServiceListItem(
    long Id,
    string ServiceCode,
    string ReferenceNumber,
    string Title,
    string ImporterName,
    string CustomerCode,
    string Mode,
    string Status,
    string RiskChannel,
    DateTime? Eta,
    int? SlaDaysLeft,
    DateTime UpdatedAt);

/// <summary>Filtered service page and the executor's overall assigned-service count.</summary>
public sealed record CaseExecutorServiceListResponse(
    int TotalAssignedCount,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<CaseExecutorServiceListItem> Items);

/// <summary>Importer information included in the case-executor workspace.</summary>
public sealed record CaseExecutorImporterResponse(
    long Id,
    string CompanyName,
    string CustomerCode,
    string Tin,
    string ContactName,
    string ContactPhone,
    string Email);

/// <summary>Staff member assigned to a service.</summary>
public sealed record CaseExecutorAssignedStaffResponse(long Id, string FullName, string RoleTitle);

/// <summary>Risk and compliance guidance for the service.</summary>
public sealed record CaseExecutorRiskComplianceResponse(string Channel, string GuidanceText);

/// <summary>One human-readable service activity event.</summary>
public sealed record CaseExecutorActivityItem(
    long Id,
    string Title,
    string Description,
    string ActorName,
    DateTime CreatedAt);

/// <summary>Logistics details shown in the case-executor workspace.</summary>
public sealed record CaseExecutorLogisticsResponse(
    string Carrier,
    string Origin,
    string Destination,
    string RouteCategory,
    DateTime? Eta,
    string ConsignmentInfo);

/// <summary>Required-document state for a service stage.</summary>
public sealed record CaseExecutorRequiredDocument(
    string DocumentType,
    string Status,
    long? DocumentId);

/// <summary>Uploaded document metadata shown in a stage.</summary>
public sealed record CaseExecutorUploadedDocument(
    long Id,
    string OriginalFileName,
    string DocumentType,
    string MimeType,
    long FileSizeBytes,
    DateTime UploadedAt,
    string UploadedByName);

/// <summary>Context carried forward from the previous stage.</summary>
public sealed record CaseExecutorPreviousStageSummary(
    string StageName,
    string Status,
    string? SummaryNote,
    DateTime? CompletedAt,
    double? DurationHours,
    string? CompletedByName);

/// <summary>Persisted workflow stage and its checklist, documents, and discussion count.</summary>
public sealed record CaseExecutorStageResponse(
    long Id,
    long StageId,
    long WorkflowStageId,
    int StageNumber,
    string Name,
    string Status,
    string RiskChannel,
    string? StageNote,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    double? DurationHours,
    string? CompletedByName,
    CaseExecutorPreviousStageSummary? PreviousStageSummary,
    IReadOnlyList<CaseExecutorRequiredDocument> RequiredDocuments,
    IReadOnlyList<CaseExecutorUploadedDocument> Documents,
    int DiscussionCount);

/// <summary>Complete assigned-service workspace for a case executor.</summary>
public sealed record CaseExecutorServiceWorkspaceResponse(
    long Id,
    string ServiceCode,
    string ReferenceNumber,
    string Title,
    string Status,
    string RiskChannel,
    string Mode,
    string Origin,
    string Destination,
    DateTime? Eta,
    int CurrentStageNumber,
    int TotalStages,
    int CompletionPercentage,
    CaseExecutorImporterResponse Importer,
    IReadOnlyList<CaseExecutorAssignedStaffResponse> AssignedStaff,
    CaseExecutorRiskComplianceResponse RiskCompliance,
    IReadOnlyList<CaseExecutorActivityItem> RecentActivity,
    CaseExecutorLogisticsResponse Logistics,
    IReadOnlyList<CaseExecutorStageResponse> Stages);
