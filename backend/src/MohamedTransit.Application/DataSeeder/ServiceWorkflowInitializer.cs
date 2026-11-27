using Microsoft.EntityFrameworkCore;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.Application.DataSeeder;

public interface IServiceWorkflowInitializer
{
    Task EnsureAllInitializedAsync(CancellationToken cancellationToken = default);
    Task EnsureInitializedAsync(long serviceId, CancellationToken cancellationToken = default);
}

public sealed class ServiceWorkflowInitializer(ApplicationDbContext context) : IServiceWorkflowInitializer
{
    public async Task EnsureAllInitializedAsync(CancellationToken cancellationToken = default)
    {
        var serviceIds = await context.Shipments
            .AsNoTracking()
            .Where(service => service.RecordStatus == RecordStatus.Active)
            .Select(service => service.Id)
            .ToListAsync(cancellationToken);

        foreach (var serviceId in serviceIds)
            await EnsureInitializedAsync(serviceId, cancellationToken);
    }

    public async Task EnsureInitializedAsync(long serviceId, CancellationToken cancellationToken = default)
    {
        var service = await context.Shipments
            .FirstOrDefaultAsync(item =>
                item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (service is null)
            return;

        var expectedStages = GetWorkflowStages(service);
        var stages = await context.ServiceStageExecutions
            .Include(stage => stage.RequiredDocuments)
            .Where(stage =>
                stage.ShipmentId == serviceId &&
                stage.RecordStatus == RecordStatus.Active)
            .ToListAsync(cancellationToken);

        var stageByType = stages
            .Where(stage => expectedStages.Any(definition => definition.Stage == stage.Stage))
            .GroupBy(stage => stage.Stage)
            .ToDictionary(group => group.Key, group => group.OrderBy(stage => stage.Id).First());
        foreach (var duplicate in stages.Where(stage =>
                     stageByType.TryGetValue(stage.Stage, out var selected) &&
                     selected.Id != stage.Id))
        {
            duplicate.RecordStatus = RecordStatus.InActive;
        }
        foreach (var invalid in stages.Where(stage =>
                     expectedStages.All(definition => definition.Stage != stage.Stage)))
        {
            invalid.RecordStatus = RecordStatus.InActive;
        }
        var changed = false;
        changed = stages.Any(stage => stage.RecordStatus != RecordStatus.Active);
        for (var index = 0; index < expectedStages.Length; index++)
        {
            var definition = expectedStages[index];
            if (!stageByType.TryGetValue(definition.Stage, out var stage))
            {
                stage = ServiceStageExecution.Create(serviceId, definition.Stage, stageNumber: index + 1);
                context.ServiceStageExecutions.Add(stage);
                stageByType.Add(definition.Stage, stage);
                changed = true;
            }

            var numberChanged = stage.StageNumber != index + 1;
            var riskChannel = string.IsNullOrWhiteSpace(stage.RiskChannel)
                ? (string.IsNullOrWhiteSpace(service.ManagerRiskChannel)
                    ? service.RiskLevel?.ToString() ?? "Unassigned"
                    : service.ManagerRiskChannel)
                : stage.RiskChannel;
            var requirementsBefore = stage.RequiredDocuments.Count;
            stage.InitializeWorkflow(index + 1, riskChannel, definition.RequiredDocuments);
            changed |= numberChanged || stage.RequiredDocuments.Count != requirementsBefore;
        }

        if (changed)
            await context.SaveChangesAsync(cancellationToken);
    }

    public static (ShipmentStage Stage, string[] RequiredDocuments)[] GetWorkflowStages(Shipment service)
        => GetWorkflowStages(
            service.ServiceType,
            service.ServiceType.HasValue ? null : service.Mode);

    public static (ShipmentStage Stage, string[] RequiredDocuments)[] GetWorkflowStages(
        ServiceType? serviceType,
        TransportMode? mode = null)
    {
        if (serviceType == ServiceType.Unimodal ||
            (!serviceType.HasValue && mode == TransportMode.UniModalRoad))
        {
            return
            [
                (ShipmentStage.LocalPermission, ["Import Permit", "Supporting Document"]),
                (ShipmentStage.Arrival, ["Air waybill", "Temperature log", "Packing list"]),
                (ShipmentStage.Clearance, ["Clearance Request", "Commercial invoice"])
            ];
        }

        return
        [
            (ShipmentStage.PrepaymentInvoice, ["Proforma Invoice", "Commercial invoice"]),
            (ShipmentStage.TransitPermission, ["Transit Permit", "Commercial invoice"]),
            (ShipmentStage.Amendment, ["Amendment Request"]),
            (ShipmentStage.DropRisk, ["Commercial invoice", "Packing list"]),
            (ShipmentStage.DeliveryOrder, ["Delivery Order", "Bill of lading"]),
            (ShipmentStage.WarehouseStatus, ["Warehouse Status Report"]),
            (ShipmentStage.Inspection, ["Inspection Report", "Packing list"]),
            (ShipmentStage.AssessmentandTaxPayment, ["Commercial invoice", "Valuation worksheet"]),
            (ShipmentStage.Emergency, ["Emergency Authorization"]),
            (ShipmentStage.ExitandStoragePayment, ["Exit Authorization", "Storage Payment Receipt"]),
            (ShipmentStage.Transportation, ["Transport License", "Delivery Note"]),
            (ShipmentStage.Clearance, ["Clearance Certificate"])
        ];
    }
}
