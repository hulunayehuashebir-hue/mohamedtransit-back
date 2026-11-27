namespace MohamedTransit.API.DTO.Manager;

/// <summary>Filters and paging options for the manager service list and export.</summary>
public sealed class ManagerServiceFilter
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

/// <summary>One service in the manager workspace list.</summary>
public sealed record ManagerServiceListItem(
    long Id,
    string ServiceCode,
    string ReferenceNumber,
    string Title,
    long ImporterId,
    string ImporterName,
    string CustomerCode,
    string Mode,
    string Status,
    string RiskChannel,
    string? CaseExecutorName,
    DateTime? Eta,
    int? SlaDaysLeft,
    DateTime UpdatedAt);

/// <summary>Paginated manager service list with the unfiltered sidebar count.</summary>
public sealed record ManagerServiceListResponse(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<ManagerServiceListItem> Items);

/// <summary>Importer information included in a service workspace.</summary>
public sealed record ManagerImporterResponse(
    long Id,
    string CompanyName,
    string CustomerCode,
    string Tin,
    string ContactName,
    string ContactPhone,
    string Email);

/// <summary>Staff member assigned to a service.</summary>
public sealed record ManagerAssignedStaffResponse(long UserId, string FullName, string RoleTitle);

/// <summary>Risk and compliance information shown to a manager.</summary>
public sealed record ManagerRiskComplianceResponse(string Channel, string GuidanceText);

/// <summary>One audit event in the service activity timeline.</summary>
public sealed record ManagerActivityResponse(
    long Id,
    string Title,
    string Description,
    string ActorName,
    DateTime CreatedAt);

/// <summary>Shipment route and consignment details.</summary>
public sealed record ManagerLogisticsResponse(
    string Carrier,
    string RouteCategory,
    DateTime? Eta,
    string CarrierOrVessel,
    string RouteSummary,
    string Origin,
    string Destination,
    string ConsignmentInfo);

/// <summary>Required-document state for a service stage.</summary>
public sealed record ManagerRequiredDocumentResponse(
    string DocumentType,
    string Status,
    long? DocumentId);

/// <summary>Uploaded document metadata shown in a service stage.</summary>
public sealed record ManagerUploadedDocumentResponse(
    long Id,
    string OriginalFileName,
    string DocumentType,
    string MimeType,
    long FileSizeBytes,
    DateTime UploadedAt,
    string UploadedByName);

/// <summary>Previous stage summary carried into the current stage.</summary>
public sealed record ManagerPreviousStageSummaryResponse(
    string StageName,
    string Status,
    string? SummaryNote,
    DateTime? CompletedAt,
    double? DurationHours,
    string? CompletedByName);

/// <summary>Stage details and documentation for the manager workspace.</summary>
public sealed record ManagerStageResponse(
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
    ManagerPreviousStageSummaryResponse? PreviousStageSummary,
    IReadOnlyList<ManagerRequiredDocumentResponse> RequiredDocuments,
    IReadOnlyList<ManagerUploadedDocumentResponse> Documents,
    int DiscussionCount);

/// <summary>Complete manager service workspace response.</summary>
public sealed record ManagerServiceWorkspaceResponse(
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
    ManagerImporterResponse Importer,
    IReadOnlyList<ManagerAssignedStaffResponse> AssignedStaff,
    ManagerRiskComplianceResponse RiskCompliance,
    IReadOnlyList<ManagerActivityResponse> RecentActivity,
    ManagerLogisticsResponse Logistics,
    IReadOnlyList<ManagerStageResponse> Stages);

/// <summary>One staff assignment candidate for service management.</summary>
public sealed record ManagerAvailableStaffResponse(long Id, string FullName, string RoleTitle);

/// <summary>Payload for assigning a case executor or assessor.</summary>
public sealed record ManagerAssignStaffRequest(long UserId);

/// <summary>Payload for saving a manager stage draft, advancing, or blocking a stage.</summary>
public sealed record ManagerUpdateStageRequest(string Action, string StageStatus, string RiskChannel, string? StageNote);

/// <summary>Payload for adding a stage discussion comment.</summary>
public sealed record ManagerStageDiscussionRequest(string Message);

/// <summary>Stage discussion comment response.</summary>
public sealed record ManagerStageDiscussionResponse(
    long Id,
    long AuthorUserId,
    string AuthorName,
    string AuthorRole,
    string Message,
    DateTime CreatedAt);

/// <summary>Payload for escalating a service to its assigned staff.</summary>
public sealed record ManagerEscalateRequest(string Reason, string? Priority);

/// <summary>Payload for notifying the importer about a service.</summary>
public sealed record ManagerNotifyImporterRequest(string? Subject, string Message);

/// <summary>Payload for sending an importer a service conversation message.</summary>
public sealed record ManagerMessageImporterRequest(string? Subject, string Message);
