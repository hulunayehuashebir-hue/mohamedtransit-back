namespace MohamedTransit.Application.DTO;

public sealed record ShipmentDto(
    long Id,
    string TrackingNumber,
    long ImporterId,
    string Description,
    string Mode,
    string AssignedHub,
    string Status,
    string Origin,
    string Destination,
    DateTime CreateAt,
    DateTime? UpdatedAt)
{
    public string ServiceCode { get; init; } = string.Empty;
    public string ReferenceNumber { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string WorkflowStatus { get; init; } = string.Empty;
    public string RiskChannel { get; init; } = string.Empty;
    public string CarrierOrVessel { get; init; } = string.Empty;
    public string LogisticsRouteSummary { get; init; } = string.Empty;
    public string RouteCategory { get; init; } = string.Empty;
    public DateTime? Eta { get; init; }
    public int CompletionPercentage { get; init; }
    public IReadOnlyList<ShipmentStageDto> Stages { get; init; } = [];
    public ShipmentImporterDto? Importer { get; init; }
    public IReadOnlyList<ShipmentAssignedStaffDto> AssignedStaff { get; init; } = [];
    public string? CaseExecutorName { get; init; }
    public string? AssessorName { get; init; }
}

/// <summary>Stage, checklist, and uploaded files included in a shipment response.</summary>
public sealed record ShipmentStageDto(
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
    DateTime? CompletedAt,
    string? CompletedByName,
    double? DurationHours,
    IReadOnlyList<ShipmentRequiredDocumentDto> RequiredDocuments,
    IReadOnlyList<ShipmentUploadedDocumentDto> Documents);

/// <summary>Required document status in a stage checklist.</summary>
public sealed record ShipmentRequiredDocumentDto(string DocumentType, string Status, long? DocumentId);

/// <summary>Document metadata attached to a shipment stage.</summary>
public sealed record ShipmentUploadedDocumentDto(long Id, string OriginalFileName, string DocumentType, DateTime UploadedAt);

/// <summary>Customer contact and company information linked to a shipment.</summary>
public sealed record ShipmentImporterDto(
    long Id,
    string CompanyName,
    string CustomerCode,
    string Tin,
    string ContactName,
    string ContactPhone,
    string Email);

/// <summary>Assigned staff details for a shipment.</summary>
public sealed record ShipmentAssignedStaffDto(long UserId, string FullName, string RoleTitle);
