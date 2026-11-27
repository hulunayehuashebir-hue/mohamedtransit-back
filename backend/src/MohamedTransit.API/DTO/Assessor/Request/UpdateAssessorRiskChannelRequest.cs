namespace MohamedTransit.API.DTO.Assessor.Request;

public sealed class UpdateAssessorRiskChannelRequest
{
    public long ShipmentId { get; set; }
    public long? StageId { get; set; }
    public string RiskChannel { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
