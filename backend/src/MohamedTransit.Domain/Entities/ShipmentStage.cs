using MohamedTransit.Domain.Common;

namespace MohamedTransit.Domain.Entities;

public class ServiceStageExecution : BaseEntity
{
    private readonly List<StageDocument> _documents = new List<StageDocument>();
    private readonly List<StageComment> _comments = new List<StageComment>();

    public ShipmentStage Stage { get; private set; }
    public int StageNumber { get; private set; }
    public StageStatus Status { get; private set; }
    public string RiskChannel { get; private set; } = "Unassigned";
    public string? StageNote { get; private set; }
    public string? SummaryNote { get; private set; }
    public long? CompletedByUserId { get; private set; }
    public string? CompletedByName { get; private set; }
    public InspectionType? InspectionType { get; private set; }
    public StageSpot? StageSpot { get; private set; }
    public string? Comments { get; private set; }
    public string? SpotComment { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? AssignedTo { get; private set; }
    public string? RiskNotes { get; private set; }
    public bool RequiresCustomerAction { get; private set; }
    public bool IsBlocked { get; private set; }
    public string? BlockReason { get; private set; }

    // Foreign Keys
    public long? ShipmentId { get; private set; }
    public long? UpdatedByUserId { get; private set; }

    // Navigation Properties
    public Shipment? Shipment { get; set; }
    public User? UpdatedByUser { get; set; }

    public IReadOnlyCollection<StageDocument> Documents => _documents.AsReadOnly();
    public IReadOnlyCollection<StageComment> StageComments => _comments.AsReadOnly();
    public ICollection<StageRequiredDocument> RequiredDocuments { get; private set; } = new List<StageRequiredDocument>();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    private ServiceStageExecution() { }

    public static ServiceStageExecution Create(
        long serviceId,
        ShipmentStage stage,
        long? updatedByUserId = null,
        int stageNumber = 0)
    {
        return new ServiceStageExecution
        {
            ShipmentId = serviceId,
            Stage = stage,
            StageNumber = stageNumber,
            Status = StageStatus.NotStarted,
            UpdatedByUserId = updatedByUserId,
            RecordStatus = RecordStatus.Active
        };
    }

    public void UpdateStatus(StageStatus status, string? comments = null, long? updatedByUserId = null)
    {
        Status = status;
        Comments = comments;
        UpdatedByUserId = updatedByUserId;

        if (status == StageStatus.InProgress && !StartedAt.HasValue)
        {
            StartedAt = DateTime.UtcNow;
        }

        if (status == StageStatus.Completed && !CompletedAt.HasValue)
        {
            CompletedAt = DateTime.UtcNow;
        }

        SetUpdated();
    }

    public void SetBlocked(bool isBlocked, string? reason = null, long? updatedByUserId = null)
    {
        IsBlocked = isBlocked;
        BlockReason = reason;
        UpdatedByUserId = updatedByUserId;
        SetUpdated();
    }

    public void SetCustomerActionRequired(bool required)
    {
        RequiresCustomerAction = required;
        SetUpdated();
    }

    public void AddRiskNotes(string notes)
    {
        RiskNotes = notes;
        SetUpdated();
    }

    public void UpdateAssessorRiskChannel(string riskChannel, long assessorId)
    {
        RiskChannel = riskChannel;
        UpdatedByUserId = assessorId;
        SetUpdated();
    }

    public void AddDocument(StageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _documents.Add(document);
    }

    public void AddComment(StageComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        _comments.Add(comment);
    }

    public void UpdateManagerDetails(
        int stageNumber,
        StageStatus status,
        string riskChannel,
        string? note,
        string? summaryNote,
        long? updatedByUserId = null,
        string? completedByName = null)
    {
        StageNumber = stageNumber;
        RiskChannel = riskChannel;
        StageNote = note;
        SummaryNote = summaryNote;
        UpdatedByUserId = updatedByUserId;
        Status = status;
        Comments = note;

        if (status is StageStatus.InProgress or StageStatus.Completed && !StartedAt.HasValue)
            StartedAt = DateTime.UtcNow;

        if (status == StageStatus.Completed)
        {
            CompletedAt ??= DateTime.UtcNow;
            CompletedByUserId = updatedByUserId;
            CompletedByName = completedByName;
        }
        else
        {
            CompletedAt = null;
            CompletedByUserId = null;
            CompletedByName = null;
        }

        IsBlocked = status == StageStatus.Blocked;
        RequiresCustomerAction = status == StageStatus.RequiresCustomerAction;
        BlockReason = IsBlocked ? note : null;
        SetUpdated();
    }

    public void InitializeWorkflow(int stageNumber, string riskChannel, IReadOnlyCollection<string> requiredDocumentTypes)
    {
        StageNumber = stageNumber;
        if (string.IsNullOrWhiteSpace(RiskChannel))
            RiskChannel = riskChannel;

        var existingTypes = RequiredDocuments
            .Select(document => document.DocumentType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var documentType in requiredDocumentTypes)
        {
            if (existingTypes.Add(documentType))
                RequiredDocuments.Add(StageRequiredDocument.Create(this, documentType));
        }
    }
}
