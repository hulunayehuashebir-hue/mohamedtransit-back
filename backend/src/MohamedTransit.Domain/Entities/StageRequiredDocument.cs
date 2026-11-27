using MohamedTransit.Domain;
using MohamedTransit.Domain.Common;

namespace MohamedTransit.Domain.Entities;

public class StageRequiredDocument : BaseEntity
{
    public long ServiceStageExecutionId { get; private set; }
    public string DocumentType { get; private set; } = string.Empty;

    public ServiceStageExecution ServiceStageExecution { get; private set; } = null!;

    private StageRequiredDocument() { }

    public static StageRequiredDocument Create(long stageId, string documentType) => new()
    {
        ServiceStageExecutionId = stageId,
        DocumentType = documentType,
        RecordStatus = RecordStatus.Active
    };

    public static StageRequiredDocument Create(ServiceStageExecution stage, string documentType) => new()
    {
        ServiceStageExecution = stage,
        DocumentType = documentType,
        RecordStatus = RecordStatus.Active
    };
}
