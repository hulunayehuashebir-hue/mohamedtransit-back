using MohamedTransit.Domain;
using MohamedTransit.Domain.Common;

namespace MohamedTransit.Domain.Entities;

public class ServiceActivityLog : BaseEntity
{
    public long ServiceId { get; private set; }
    public long? StageId { get; private set; }
    public string ActionTitle { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public long? ActorUserId { get; private set; }
    public string ActorName { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private ServiceActivityLog() { }

    public static ServiceActivityLog Create(
        long serviceId,
        long? stageId,
        string actionTitle,
        string description,
        long? actorUserId,
        string actorName) => new()
        {
            ServiceId = serviceId,
            StageId = stageId,
            ActionTitle = actionTitle,
            Description = description,
            ActorUserId = actorUserId,
            ActorName = actorName,
            CreatedAt = DateTime.UtcNow,
            RecordStatus = RecordStatus.Active
        };
}
