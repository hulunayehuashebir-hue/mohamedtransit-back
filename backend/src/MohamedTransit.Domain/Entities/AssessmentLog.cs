using MohamedTransit.Domain.Common;

namespace MohamedTransit.Domain.Entities;

public class AssessmentLog : BaseEntity
{
    public long ActorUserId { get; private set; }
    public string ActorName { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public long? CustomerId { get; private set; }
    public long? DocumentId { get; private set; }
    public long? ServiceId { get; private set; }
    public long? StageId { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }

    private AssessmentLog() { }

    public static AssessmentLog Create(
        long actorUserId,
        string actorName,
        string action,
        string targetType,
        string reference,
        string description,
        long? customerId = null,
        long? documentId = null,
        long? serviceId = null,
        long? stageId = null) => new()
        {
            ActorUserId = actorUserId,
            ActorName = actorName,
            Action = action,
            TargetType = targetType,
            Reference = reference,
            Description = description,
            CustomerId = customerId,
            DocumentId = documentId,
            ServiceId = serviceId,
            StageId = stageId,
            OccurredAt = DateTime.UtcNow,
            RecordStatus = RecordStatus.Active
        };
}
