using MediatR;

using Microsoft.EntityFrameworkCore;

using MohamedTransit.Application.DTO;
using MohamedTransit.Application.Queries.Shipment;
using MohamedTransit.Application.DataSeeder;
using MohamedTransit.Application.DTO;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.Application.Handlers.Shipment;

public class GetShipmentByIdQueryHandler : IRequestHandler<GetShipmentByIdQuery, ShipmentDto?>
{
    private readonly ApplicationDbContext _context;
    private readonly IServiceWorkflowInitializer _workflowInitializer;

    public GetShipmentByIdQueryHandler(
        ApplicationDbContext context,
        IServiceWorkflowInitializer workflowInitializer)
    {
        _context = context;
        _workflowInitializer = workflowInitializer;
    }

    public async Task<ShipmentDto?> Handle(GetShipmentByIdQuery request, CancellationToken cancellationToken)
    {
        await _workflowInitializer.EnsureInitializedAsync(request.Id, cancellationToken);
        var shipment = await _context.Shipments
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Importer)
            .Include(item => item.AssignedCaseExecutor)
            .Include(item => item.AssignedAssessor)
            .Include(item => item.Stages)
                .ThenInclude(stage => stage.RequiredDocuments)
            .Include(item => item.Stages)
                .ThenInclude(stage => stage.Documents)
            .Include(item => item.Documents)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken);

        if (shipment is null)
        {
            return null;
        }

        var customer = await _context.Customers
            .AsNoTracking()
            .Where(item => item.UserId == shipment.ImporterId && item.RecordStatus == RecordStatus.Active)
            .OrderByDescending(item => item.CreateAt)
            .Select(item => new
            {
                item.Id,
                item.BusinessName,
                item.TINNumber,
                item.ContactPerson,
                item.ContactPhone,
                item.ContactEmail
            })
            .FirstOrDefaultAsync(cancellationToken);

        var stages = shipment.Stages
            .Where(stage => stage.RecordStatus == RecordStatus.Active)
            .OrderBy(stage => stage.StageNumber)
            .ThenBy(stage => stage.Id)
            .Select((stage, index) =>
            {
                var requiredDocuments = stage.RequiredDocuments
                    .Where(required => required.RecordStatus == RecordStatus.Active)
                    .Select(required =>
                    {
                        var doc = shipment.Documents
                            .Where(document =>
                                document.RecordStatus == RecordStatus.Active &&
                                document.ShipmentStageId == stage.Id)
                            .FirstOrDefault(document =>
                                string.Equals(
                                    DocumentTypeName(document.DocumentType, document.DocumentTypeLabel),
                                    required.DocumentType,
                                    StringComparison.OrdinalIgnoreCase));
                        return new ShipmentRequiredDocumentDto(
                            required.DocumentType,
                            doc is null ? "Outstanding" : "Received",
                            doc?.Id);
                    })
                    .ToList();
                var uploadedDocuments = shipment.Documents
                    .Where(document =>
                        document.RecordStatus == RecordStatus.Active &&
                        document.ShipmentStageId == stage.Id)
                    .Select(document => new ShipmentUploadedDocumentDto(
                        document.Id,
                        document.OriginalFileName,
                        DocumentTypeName(document.DocumentType, document.DocumentTypeLabel),
                        document.CreateAt))
                    .Concat(stage.Documents
                        .Where(document => document.RecordStatus == RecordStatus.Active)
                        .Select(document => new ShipmentUploadedDocumentDto(
                            document.Id,
                            document.OriginalFileName,
                            DocumentTypeName(document.DocumentType, document.DocumentTypeLabel),
                            document.CreateAt)))
                    .ToList();

                return new ShipmentStageDto(
                    stage.Id,
                    stage.Id,
                    stage.Id,
                    stage.StageNumber > 0 ? stage.StageNumber : index + 1,
                    StageName(stage.Stage),
                    stage.Status == StageStatus.Pending ? StageStatus.NotStarted.ToString() : stage.Status.ToString(),
                    stage.RiskChannel,
                    stage.StageNote ?? stage.Comments,
                    stage.CreateAt,
                    stage.UpdatedAt,
                    stage.CompletedAt,
                    stage.CompletedByName,
                    stage.StartedAt.HasValue
                        ? Math.Max(0, ((stage.CompletedAt ?? DateTime.UtcNow) - stage.StartedAt.Value).TotalHours)
                        : null,
                    requiredDocuments,
                    uploadedDocuments);
            })
            .ToList();

        var assignedStaff = new List<ShipmentAssignedStaffDto>();
        if (shipment.AssignedCaseExecutorId.HasValue && shipment.AssignedCaseExecutor is not null)
            assignedStaff.Add(new ShipmentAssignedStaffDto(
                shipment.AssignedCaseExecutorId.Value,
                FullName(shipment.AssignedCaseExecutor.FirstName, shipment.AssignedCaseExecutor.LastName),
                "Case Executor"));
        if (shipment.AssignedAssessorId.HasValue && shipment.AssignedAssessor is not null)
            assignedStaff.Add(new ShipmentAssignedStaffDto(
                shipment.AssignedAssessorId.Value,
                FullName(shipment.AssignedAssessor.FirstName, shipment.AssignedAssessor.LastName),
                "KYC / Valuation Assessor"));

        return new ShipmentDto(
            shipment.Id,
            shipment.TrackingNumber, // .Value ተወግዷል
            shipment.ImporterId,
            shipment.Description,
            shipment.Mode.ToString(),
            shipment.AssignedHub.ToString(),
            shipment.Status.ToString(),
            shipment.Origin,         // shipment.Route.Origin የነበረው ተስተካክሏል
            shipment.Destination,    // shipment.Route.Destination የነበረው ተስተካክሏል
            shipment.CreateAt,
            shipment.UpdatedAt)
        {
            ServiceCode = ValueOr(shipment.ServiceCode, shipment.TrackingNumber),
            ReferenceNumber = ValueOr(shipment.ReferenceNumber, shipment.TrackingNumber),
            Title = ValueOr(shipment.Title, shipment.Description),
            WorkflowStatus = string.IsNullOrWhiteSpace(shipment.WorkflowStatus)
                ? shipment.Status == ShipmentStatus.Submitted ? "Pending" : shipment.Status.ToString()
                : shipment.WorkflowStatus,
            RiskChannel = string.IsNullOrWhiteSpace(shipment.ManagerRiskChannel)
                ? shipment.RiskLevel?.ToString() ?? "Unassigned"
                : shipment.ManagerRiskChannel,
            CarrierOrVessel = shipment.CarrierOrVessel,
            LogisticsRouteSummary = shipment.LogisticsRouteSummary,
            RouteCategory = shipment.RouteCategory,
            Eta = shipment.ETA,
            CompletionPercentage = shipment.CompletionPercentage,
            Stages = stages,
            Importer = customer is null
                ? null
                : new ShipmentImporterDto(
                    customer.Id,
                    customer.BusinessName,
                    $"CUS-{customer.Id:D6}",
                    customer.TINNumber,
                    customer.ContactPerson,
                    customer.ContactPhone,
                    customer.ContactEmail),
            AssignedStaff = assignedStaff
        };
    }

    private static string DocumentTypeName(DocumentType documentType, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
            return label;
        return documentType switch
        {
            DocumentType.Invoice => "Commercial invoice",
            DocumentType.ArrivalPhoto => "Air waybill",
            DocumentType.LegalDocument => "Supporting Document",
            DocumentType.DeliveryOrder => "Delivery Order",
            DocumentType.InspectionReport => "Inspection Report",
            DocumentType.ClearanceDocument => "Clearance Certificate",
            _ => documentType.ToString()
        };
    }

    private static string StageName(ShipmentStage stage) => stage switch
    {
        ShipmentStage.AssessmentandTaxPayment => "Assessment & Tax Payment",
        ShipmentStage.Emergency => "Emergency / Spot",
        ShipmentStage.ExitandStoragePayment => "Exit & Storage Payment",
        ShipmentStage.LocalPermission => "Local Permission",
        ShipmentStage.TransitPermission => "Transit Permission",
        ShipmentStage.DeliveryOrder => "Delivery Order",
        ShipmentStage.WarehouseStatus => "Warehouse Status",
        ShipmentStage.DropRisk => "Drop Risk",
        ShipmentStage.PrepaymentInvoice => "Prepayment Invoice",
        ShipmentStage.Amendment => "Amendment",
        ShipmentStage.Inspection => "Inspection",
        ShipmentStage.Transportation => "Transportation",
        ShipmentStage.Arrival => "Arrival",
        ShipmentStage.Clearance => "Clearance Request",
        _ => stage.ToString()
    };

    private static string FullName(string? firstName, string? lastName) =>
        string.Join(" ", new[] { firstName, lastName }.Where(name => !string.IsNullOrWhiteSpace(name))).Trim();

    private static string ValueOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
