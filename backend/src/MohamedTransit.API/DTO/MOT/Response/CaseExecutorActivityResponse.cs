namespace MohamedTransit.API.DTO.MOT.Response;

public class CaseExecutorActivityResponse
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public long? ServiceId { get; set; }
    public DateTime Timestamp { get; set; }
}
