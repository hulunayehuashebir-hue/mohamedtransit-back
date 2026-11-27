namespace MohamedTransit.API.DTO.Assessor.Request;

public class DownloadAssessorStageDocumentsRequest
{
    public long ServiceId { get; set; }
    public long StageId { get; set; }
    public List<long> DocumentIds { get; set; } = new();
}
