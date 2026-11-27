using MediatR;

using Microsoft.EntityFrameworkCore;

using MohamedTransit.Application;
using MohamedTransit.Application.Helper;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.Application.Handlers.ShipmentStageHandler;

internal class UpdateServiceStageCommandHandler : IRequestHandler<UpdateServiceStageCommand, OperationResult<ServiceStageExecution>>
{
    private readonly ApplicationDbContext _context;

    public UpdateServiceStageCommandHandler(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<OperationResult<ServiceStageExecution>> Handle(UpdateServiceStageCommand request, CancellationToken cancellationToken)
    {
        var result = new OperationResult<ServiceStageExecution>();

        // 1. ServiceStageExecution መኖሩን ከነ ShipmentId ማረጋገጥ
        // (ማስታወሻ፡ በ ApplicationDbContext ላይ ያለው DbSet 'ServiceStageExecutions' ከሆነ እሱን ተቀቀሚ)
        var stageExecution = await _context.ServiceStageExecutions
            .FirstOrDefaultAsync(s => s.Id == request.ShipmentStageId && s.ShipmentId == request.ShipmentId, cancellationToken);

        if (stageExecution == null)
        {
            result.AddError(ErrorCode.NotFound, $"Stage execution with ID '{request.ShipmentStageId}' for Shipment '{request.ShipmentId}' was not found.");
            return result;
        }

        // 2. የሚያሻሽለው ተጠቃሚ (UpdatedByUserId) በዳታቤዝ ውስጥ መኖሩን ማረጋገጥ
        var userExists = await _context.Users
            .AnyAsync(u => u.Id == request.UpdatedByUserId, cancellationToken);

        if (!userExists)
        {
            result.AddError(ErrorCode.NotFound, $"User with ID '{request.UpdatedByUserId}' was not found.");
            return result;
        }

        // 3. Status እና Comments/Notes ማሻሻል (የ Entityው UpdateStatus method በመጠቀም)
        stageExecution.UpdateStatus(request.Status, request.Notes, request.UpdatedByUserId);
        stageExecution.SetBlocked(
            request.Status == StageStatus.Blocked,
            request.Status == StageStatus.Blocked ? request.Notes : null,
            request.UpdatedByUserId);
        stageExecution.SetCustomerActionRequired(request.Status == StageStatus.RequiresCustomerAction);

        var service = await _context.Shipments
            .FirstOrDefaultAsync(shipment => shipment.Id == request.ShipmentId, cancellationToken);
        if (service is null)
        {
            result.AddError(ErrorCode.NotFound, $"Shipment '{request.ShipmentId}' was not found.");
            return result;
        }

        var stages = await _context.ServiceStageExecutions
            .Where(stage =>
                stage.ShipmentId == request.ShipmentId &&
                stage.RecordStatus == RecordStatus.Active)
            .ToListAsync(cancellationToken);
        var completedCount = stages.Count(stage => stage.Status == StageStatus.Completed);
        var overallStatus = stages.Any(stage => stage.Status == StageStatus.Blocked || stage.IsBlocked)
            ? "Blocked"
            : stages.Any(stage => stage.Status == StageStatus.RequiresCustomerAction || stage.RequiresCustomerAction)
                ? "RequiresCustomerAction"
                : stages.Any(stage => stage.Status == StageStatus.NeedsReview)
                    ? "NeedsReview"
                    : stages.Count > 0 && completedCount == stages.Count
                        ? "Completed"
                        : "InProgress";
        service.UpdateManagerWorkspace(
            string.IsNullOrWhiteSpace(service.ServiceCode) ? service.TrackingNumber : service.ServiceCode,
            string.IsNullOrWhiteSpace(service.ReferenceNumber) ? service.TrackingNumber : service.ReferenceNumber,
            string.IsNullOrWhiteSpace(service.Title) ? service.Description : service.Title,
            overallStatus,
            string.IsNullOrWhiteSpace(service.ManagerRiskChannel)
                ? service.RiskLevel?.ToString() ?? "Green"
                : service.ManagerRiskChannel,
            service.ETA,
            service.SlaDeadline,
            service.CarrierOrVessel,
            service.LogisticsRouteSummary,
            service.ConsignmentDetails,
            stages.Count == 0 ? 0 : (int)Math.Round(100d * completedCount / stages.Count));

        var actorName = await _context.Users
            .Where(user => user.Id == request.UpdatedByUserId)
            .Select(user => user.FirstName + " " + user.LastName)
            .FirstAsync(cancellationToken);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            request.ShipmentId,
            request.ShipmentStageId,
            request.Status == StageStatus.Blocked ? "Stage blocked" : "Stage updated",
            $"Status changed to {request.Status}. {request.Notes}".Trim(),
            request.UpdatedByUserId,
            actorName.Trim()));

        // 4. ለውጦችን Save ማድረግ
        await _context.SaveChangesAsync(cancellationToken);

        // 5. Result መመለስ
        result.Payload = stageExecution;
        result.Message = "Stage execution status updated successfully.";

        return result;
    }
}
