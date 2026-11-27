namespace MohamedTransit.API.DTO.Assessor.Request;

public sealed class CustomerAssessmentDecisionRequest
{
    public string Decision { get; set; } = string.Empty;
    public string? AssessorNote { get; set; }
}
