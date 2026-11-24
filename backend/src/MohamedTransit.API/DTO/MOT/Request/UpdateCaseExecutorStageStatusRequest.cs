using MohamedTransit.Domain.Common;

namespace MohamedTransit.API.DTO.MOT.Request;

/// <summary>
/// Updates the status of a stage belonging to the authenticated case executor's service.
/// </summary>
public sealed record UpdateCaseExecutorStageStatusRequest(
    long ShipmentId,
    long StageId,
    StageStatus Status,
    string? Notes);
