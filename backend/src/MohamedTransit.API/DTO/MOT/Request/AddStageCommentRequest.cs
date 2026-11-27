namespace MohamedTransit.API.DTO.MOT.Request;
public class AddStageCommentRequest
{
    public long ServiceId { get; set; }
    public long ShipmentId { get; set; }
    public long StageId { get; set; }
    public long WorkflowStageId { get; set; }
    public string? Comment { get; set; }
    public string? Message { get; set; }
    public string? CommentType { get; set; }
    public bool IsInternal { get; set; } = false;
    public bool IsVisibleToCustomer { get; set; } = true;

    public long EffectiveServiceId => ServiceId > 0 ? ServiceId : ShipmentId;
    public long EffectiveStageId => WorkflowStageId > 0 ? WorkflowStageId : StageId;
    public string EffectiveComment =>
        !string.IsNullOrWhiteSpace(Comment) ? Comment : Message ?? string.Empty;
}

