namespace MohamedTransit.API.DTO.Assessor.Request;

public sealed class DecideServiceStageRequest
{
    public long ServiceId { get; set; }
    public long StageId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
