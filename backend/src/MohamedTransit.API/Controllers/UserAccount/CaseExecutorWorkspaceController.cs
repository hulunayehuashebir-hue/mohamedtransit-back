using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MohamedTransit.API.DTO.MOT.Request;
using MohamedTransit.API.DTO.MOT.Response;
using MohamedTransit.API.Helpers;
using MohamedTransit.Application.DataSeeder;
using MohamedTransit.Application.Services;
using MohamedTransit.Domain;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.API.Controllers;

public partial class CaseExecutorController
{
    private const long MaximumStageDocumentBytes = 25 * 1024 * 1024;
    private readonly IWebHostEnvironment _environment;
    private readonly IServiceWorkflowInitializer _workflowInitializer;
    private readonly IServiceProvider _serviceProvider;

    [HttpGet("services")]
    [ProducesResponseType(typeof(CaseExecutorServiceListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkspaceServices(
        [FromQuery] CaseExecutorServiceFilter filter,
        CancellationToken cancellationToken)
    {
        var (userId, error) = await RequireCaseExecutorAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!IsValidCaseExecutorFilter(filter, out var validationError))
            return BadRequest(validationError);

        var assignedServices = _context.Shipments
            .AsNoTracking()
            .Where(shipment =>
                shipment.AssignedCaseExecutorId == userId &&
                shipment.RecordStatus == RecordStatus.Active);
        var totalAssignedCount = await assignedServices.CountAsync(cancellationToken);
        var filtered = ApplyCaseExecutorFilters(assignedServices, filter);
        var totalCount = await filtered.CountAsync(cancellationToken);
        var rows = await ApplyCaseExecutorSort(filtered, filter)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.ServiceCode,
                shipment.TrackingNumber,
                shipment.ReferenceNumber,
                shipment.Title,
                shipment.Description,
                shipment.ImporterId,
                shipment.ServiceType,
                shipment.Mode,
                shipment.WorkflowStatus,
                shipment.Status,
                shipment.ManagerRiskChannel,
                shipment.RiskLevel,
                shipment.ETA,
                shipment.SlaDeadline,
                shipment.UpdatedAt,
                shipment.CreateAt,
                Customer = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new { customer.Id, customer.BusinessName })
                    .FirstOrDefault(),
                ImporterName = shipment.Importer.FirstName + " " + shipment.Importer.LastName
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var items = rows.Select(row => new CaseExecutorServiceListItem(
            row.Id,
            ValueOr(row.ServiceCode, row.TrackingNumber),
            ValueOr(row.ReferenceNumber, row.TrackingNumber),
            ValueOr(row.Title, row.Description),
            ValueOr(row.Customer?.BusinessName, row.ImporterName),
            CustomerCode(row.Customer?.Id ?? row.ImporterId),
            ResolveMode(row.ServiceType, row.Mode),
            ResolveServiceStatus(row.WorkflowStatus, row.Status),
            ResolveRiskChannel(row.ManagerRiskChannel, row.RiskLevel),
            row.ETA,
            row.SlaDeadline.HasValue
                ? (int)Math.Ceiling((row.SlaDeadline.Value - now).TotalDays)
                : null,
            row.UpdatedAt ?? row.CreateAt))
            .ToList();

        return Ok(new CaseExecutorServiceListResponse(
            totalAssignedCount,
            totalCount,
            filter.Page,
            filter.PageSize,
            items));
    }

    [HttpGet("services/export")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportWorkspaceServices(
        [FromQuery] CaseExecutorServiceFilter filter,
        CancellationToken cancellationToken)
    {
        var (userId, error) = await RequireCaseExecutorAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!IsValidCaseExecutorFilter(filter, out var validationError, validatePaging: false))
            return BadRequest(validationError);

        var query = ApplyCaseExecutorFilters(
            _context.Shipments
                .AsNoTracking()
                .Where(shipment =>
                    shipment.AssignedCaseExecutorId == userId &&
                    shipment.RecordStatus == RecordStatus.Active),
            filter);
        var rows = await ApplyCaseExecutorSort(query, filter)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.ServiceCode,
                shipment.TrackingNumber,
                shipment.ReferenceNumber,
                shipment.Title,
                shipment.Description,
                shipment.ImporterId,
                shipment.ServiceType,
                shipment.Mode,
                shipment.WorkflowStatus,
                shipment.Status,
                shipment.ManagerRiskChannel,
                shipment.RiskLevel,
                shipment.ETA,
                shipment.SlaDeadline,
                shipment.UpdatedAt,
                shipment.CreateAt,
                Customer = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new { customer.Id, customer.BusinessName })
                    .FirstOrDefault(),
                ImporterName = shipment.Importer.FirstName + " " + shipment.Importer.LastName
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var csv = new StringBuilder(
            "id,serviceCode,referenceNumber,title,importerName,customerCode,mode,status,riskChannel,eta,slaDaysLeft,updatedAt\r\n");
        foreach (var row in rows)
        {
            var values = new[]
            {
                row.Id.ToString(CultureInfo.InvariantCulture),
                ValueOr(row.ServiceCode, row.TrackingNumber),
                ValueOr(row.ReferenceNumber, row.TrackingNumber),
                ValueOr(row.Title, row.Description),
                ValueOr(row.Customer?.BusinessName, row.ImporterName),
                CustomerCode(row.Customer?.Id ?? row.ImporterId),
                ResolveMode(row.ServiceType, row.Mode),
                ResolveServiceStatus(row.WorkflowStatus, row.Status),
                ResolveRiskChannel(row.ManagerRiskChannel, row.RiskLevel),
                row.ETA?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.SlaDeadline.HasValue
                    ? Math.Ceiling((row.SlaDeadline.Value - now).TotalDays).ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                (row.UpdatedAt ?? row.CreateAt).ToString("O", CultureInfo.InvariantCulture)
            };
            csv.AppendLine(string.Join(",", values.Select(CsvEscape)));
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "case-executor-services.csv");
    }

    [HttpGet("services/{serviceIdOrCode}")]
    [ProducesResponseType(typeof(CaseExecutorServiceWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkspace(
        string serviceIdOrCode,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (!userId.HasValue)
            return Unauthorized("User not authenticated.");

        var service = await FindServiceAsync(serviceIdOrCode, cancellationToken);
        if (service is null)
            return NotFound("Service not found.");

        var isExecutor = await IsCaseExecutor(userId.Value, cancellationToken);
        var isManager = await IsManagerOrAdmin(userId.Value, cancellationToken);
        if (!isExecutor && !isManager)
            return Forbid();
        if (isExecutor && service.AssignedCaseExecutorId != userId.Value && !isManager)
            return Forbid();

        await _workflowInitializer.EnsureInitializedAsync(service.Id, cancellationToken);
        service = await _context.Shipments
            .AsNoTracking()
            .Include(item => item.Importer)
            .Include(item => item.AssignedCaseExecutor)
            .Include(item => item.AssignedAssessor)
            .FirstAsync(item => item.Id == service.Id, cancellationToken);

        var customer = await _context.Customers
            .AsNoTracking()
            .Where(item =>
                item.UserId == service.ImporterId &&
                item.RecordStatus == RecordStatus.Active)
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

        var expectedOrder = WorkflowStages(service.ServiceType, service.Mode);
        var stageRows = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Where(stage =>
                stage.ShipmentId == service.Id &&
                stage.RecordStatus == RecordStatus.Active &&
                expectedOrder.Contains(stage.Stage))
            .Select(stage => new
            {
                Stage = stage,
                RequiredDocuments = stage.RequiredDocuments
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .Select(document => new { document.Id, document.DocumentType })
                    .ToList()
            })
            .ToListAsync(cancellationToken);
        stageRows = stageRows
            .OrderBy(row => Array.IndexOf(expectedOrder, row.Stage.Stage))
            .ToList();

        var stageIds = stageRows.Select(row => row.Stage.Id).ToArray();
        var stageDocuments = await _context.StageDocuments
            .AsNoTracking()
            .Where(document =>
                document.ServiceStageId.HasValue &&
                stageIds.Contains(document.ServiceStageId.Value) &&
                document.RecordStatus == RecordStatus.Active)
            .Select(document => new
            {
                document.Id,
                StageId = document.ServiceStageId!.Value,
                document.OriginalFileName,
                document.DocumentTypeLabel,
                document.DocumentType,
                document.MimeType,
                document.FileSizeBytes,
                document.CreateAt,
                UploadedByName = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
            })
            .ToListAsync(cancellationToken);
        var serviceDocuments = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.ShipmentStageId.HasValue &&
                stageIds.Contains(document.ShipmentStageId.Value) &&
                document.RecordStatus == RecordStatus.Active)
            .Select(document => new
            {
                document.Id,
                StageId = document.ShipmentStageId!.Value,
                document.OriginalFileName,
                document.DocumentTypeLabel,
                document.DocumentType,
                document.MimeType,
                document.FileSizeBytes,
                document.CreateAt,
                UploadedByName = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
            })
            .ToListAsync(cancellationToken);
        var documents = stageDocuments
            .Select(document => new CaseExecutorDocumentRow(
                document.Id,
                document.StageId,
                document.OriginalFileName,
                DocumentTypeName(document.DocumentType, document.DocumentTypeLabel),
                document.MimeType,
                document.FileSizeBytes,
                document.CreateAt,
                document.UploadedByName))
            .Concat(serviceDocuments.Select(document => new CaseExecutorDocumentRow(
                document.Id,
                document.StageId,
                document.OriginalFileName,
                DocumentTypeName(document.DocumentType, document.DocumentTypeLabel),
                document.MimeType,
                document.FileSizeBytes,
                document.CreateAt,
                document.UploadedByName)))
            .ToList();
        var discussionCounts = await _context.StageDiscussions
            .AsNoTracking()
            .Where(discussion =>
                discussion.ServiceId == service.Id &&
                discussion.RecordStatus == RecordStatus.Active &&
                stageIds.Contains(discussion.StageId))
            .GroupBy(discussion => discussion.StageId)
            .Select(group => new { StageId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.StageId, group => group.Count, cancellationToken);

        var workspaceStages = stageRows.Select((row, index) =>
        {
            var stage = row.Stage;
            var stageFiles = documents.Where(document => document.StageId == stage.Id).ToList();
            var requiredDocuments = row.RequiredDocuments.Select(required =>
            {
                var matchingDocument = stageFiles.FirstOrDefault(document =>
                    string.Equals(
                        document.DocumentType,
                        required.DocumentType,
                        StringComparison.OrdinalIgnoreCase));
                return new CaseExecutorRequiredDocument(
                    required.DocumentType,
                    matchingDocument is null ? "Outstanding" : "Received",
                    matchingDocument?.Id);
            }).ToList();
            var previous = stageRows
                .Take(index)
                .LastOrDefault(previousRow => previousRow.Stage.Status == StageStatus.Completed)
                ?.Stage;
            return new CaseExecutorStageResponse(
                stage.Id,
                stage.Id,
                stage.Id,
                stage.StageNumber > 0 ? stage.StageNumber : index + 1,
                StageName(stage.Stage),
                FormatStageStatus(stage.Status),
                ValueOr(stage.RiskChannel, "Unassigned"),
                stage.StageNote ?? stage.Comments,
                stage.CreateAt,
                stage.UpdatedAt,
                stage.StartedAt,
                stage.CompletedAt,
                DurationHours(stage.StartedAt, stage.CompletedAt),
                stage.CompletedByName,
                previous is null
                    ? null
                    : new CaseExecutorPreviousStageSummary(
                        StageName(previous.Stage),
                        FormatStageStatus(previous.Status),
                        previous.SummaryNote,
                        previous.CompletedAt,
                        DurationHours(previous.StartedAt, previous.CompletedAt),
                        previous.CompletedByName),
                requiredDocuments,
                stageFiles.Select(document => new CaseExecutorUploadedDocument(
                    document.Id,
                    document.OriginalFileName,
                    document.DocumentType,
                    document.MimeType,
                    document.FileSizeBytes,
                    document.CreatedAt,
                    document.UploadedByName)).ToList(),
                discussionCounts.GetValueOrDefault(stage.Id));
        }).ToList();

        var activityRows = await _context.ServiceActivityLogs
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == service.Id &&
                item.RecordStatus == RecordStatus.Active)
            .OrderByDescending(item => item.CreatedAt)
            .Take(25)
            .Select(item => new
            {
                item.Id,
                item.ActionTitle,
                item.Description,
                item.ActorName,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var activity = activityRows.Select(item => new CaseExecutorActivityItem(
            item.Id,
            item.ActionTitle,
            HumanizeActivityText(item.Description),
            item.ActorName,
            item.CreatedAt)).ToList();
        var assignedStaff = new List<CaseExecutorAssignedStaffResponse>();
        if (service.AssignedCaseExecutorId.HasValue && service.AssignedCaseExecutor is not null)
        {
            assignedStaff.Add(new CaseExecutorAssignedStaffResponse(
                service.AssignedCaseExecutorId.Value,
                FullName(service.AssignedCaseExecutor.FirstName, service.AssignedCaseExecutor.LastName),
                "Case Executor"));
        }
        if (service.AssignedAssessorId.HasValue && service.AssignedAssessor is not null)
        {
            assignedStaff.Add(new CaseExecutorAssignedStaffResponse(
                service.AssignedAssessorId.Value,
                FullName(service.AssignedAssessor.FirstName, service.AssignedAssessor.LastName),
                "KYC / Valuation Assessor"));
        }

        var currentStage = workspaceStages.FirstOrDefault(stage => stage.Status != StageStatus.Completed.ToString());
        var completedCount = workspaceStages.Count(stage => stage.Status == StageStatus.Completed.ToString());
        var completion = workspaceStages.Count == 0
            ? service.CompletionPercentage
            : (int)Math.Round(100d * completedCount / workspaceStages.Count);
        var riskChannel = ResolveRiskChannel(service.ManagerRiskChannel, service.RiskLevel);
        var importerName = FullName(service.Importer.FirstName, service.Importer.LastName);

        return Ok(new CaseExecutorServiceWorkspaceResponse(
            service.Id,
            ValueOr(service.ServiceCode, service.TrackingNumber),
            ValueOr(service.ReferenceNumber, service.TrackingNumber),
            ValueOr(service.Title, service.Description),
            ResolveServiceStatus(service.WorkflowStatus, service.Status),
            riskChannel,
            ResolveMode(service.ServiceType, service.Mode),
            service.Origin,
            service.Destination,
            service.ETA,
            currentStage?.StageNumber ?? workspaceStages.Count,
            expectedOrder.Length,
            completion,
            new CaseExecutorImporterResponse(
                service.ImporterId,
                ValueOr(customer?.BusinessName, importerName),
                CustomerCode(customer?.Id ?? service.ImporterId),
                customer?.TINNumber ?? string.Empty,
                ValueOr(customer?.ContactPerson, importerName),
                ValueOr(customer?.ContactPhone, service.Importer.Phone),
                ValueOr(customer?.ContactEmail, service.Importer.Email)),
            assignedStaff,
            new CaseExecutorRiskComplianceResponse(riskChannel, RiskGuidance(riskChannel)),
            activity,
            new CaseExecutorLogisticsResponse(
                service.CarrierOrVessel,
                service.Origin,
                service.Destination,
                service.RouteCategory,
                service.ETA,
                service.ConsignmentDetails),
            workspaceStages));
    }

    [HttpPost("services/{serviceIdOrCode}/stages/{stageId:long}/update")]
    public async Task<IActionResult> UpdateWorkspaceStage(
        string serviceIdOrCode,
        long stageId,
        [FromBody] UpdateCaseExecutorStageRequest request,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (request.StageId.HasValue && request.StageId.Value != stageId)
            return BadRequest("stageId in the request body must match the stageId in the URL.");
        if (string.IsNullOrWhiteSpace(request.Action))
            return BadRequest("action is required.");
        if (!IsRiskChannel(request.RiskChannel))
            return BadRequest("riskChannel must be Green, Yellow, Blue, or Red.");
        var action = request.Action.Trim().ToLowerInvariant();
        if (action is not ("draft" or "advance" or "block"))
            return BadRequest("action must be draft, advance, or block.");
        if (action != "advance" && !TryParseStageStatus(request.StageStatus, out _))
            return BadRequest("stageStatus must be NotStarted, InProgress, NeedsReview, RequiresCustomerAction, Completed, or Blocked.");
        if (action == "block" && string.IsNullOrWhiteSpace(request.StageNote))
            return BadRequest("stageNote is required when blocking a stage.");

        await _workflowInitializer.EnsureInitializedAsync(service!.Id, cancellationToken);
        var orderedStages = await _context.ServiceStageExecutions
            .Where(stage =>
                stage.ShipmentId == service!.Id &&
                stage.RecordStatus == RecordStatus.Active)
            .OrderBy(stage => stage.StageNumber)
            .ThenBy(stage => stage.Id)
            .ToListAsync(cancellationToken);
        var stageIndex = orderedStages.FindIndex(stage => stage.Id == stageId);
        if (stageIndex < 0)
            return NotFound("Assigned service stage not found.");
        var stage = orderedStages[stageIndex];
        var next = stageIndex + 1 < orderedStages.Count ? orderedStages[stageIndex + 1] : null;
        var actorName = await GetExecutorNameAsync(userId, cancellationToken);
        var stageNote = request.StageNote?.Trim();
        var riskChannel = request.RiskChannel.Trim();

        var targetStatus = action switch
        {
            "advance" => StageStatus.Completed,
            "block" => StageStatus.Blocked,
            _ => ParseStageStatus(request.StageStatus)
        };
        var requiresAssessorReview = targetStatus == StageStatus.Completed &&
            (riskChannel.Equals("Yellow", StringComparison.OrdinalIgnoreCase) ||
             riskChannel.Equals("Red", StringComparison.OrdinalIgnoreCase)) &&
            (!service!.AssignedAssessorId.HasValue ||
             stage.CompletedByUserId != service.AssignedAssessorId.Value);

        var status = requiresAssessorReview
            ? StageStatus.NeedsReview
            : targetStatus;
        var summary = status == StageStatus.Completed
            ? stageNote ?? stage.SummaryNote
            : stage.SummaryNote;
        stage.UpdateManagerDetails(
            stage.StageNumber > 0 ? stage.StageNumber : stageIndex + 1,
            status,
            riskChannel,
            requiresAssessorReview
                ? ValueOr(stageNote, "Assessor sign-off is required before this stage can advance.")
                : stageNote,
            summary,
            userId,
            actorName);

        if (action == "advance" && !requiresAssessorReview && next is not null &&
            next.Status != StageStatus.Completed)
        {
            next.UpdateManagerDetails(
                next.StageNumber > 0 ? next.StageNumber : stageIndex + 2,
                StageStatus.InProgress,
                next.RiskChannel,
                next.StageNote,
                next.SummaryNote,
                userId,
                actorName);
        }

        var completedCount = orderedStages.Count(item => item.Status == StageStatus.Completed);
        var serviceStatus = ResolveAggregateStatus(orderedStages, completedCount);
        service!.UpdateManagerWorkspace(
            ValueOr(service.ServiceCode, service.TrackingNumber),
            ValueOr(service.ReferenceNumber, service.TrackingNumber),
            ValueOr(service.Title, service.Description),
            requiresAssessorReview ? "NeedsReview" : serviceStatus,
            riskChannel,
            service.ETA,
            service.SlaDeadline,
            service.CarrierOrVessel,
            service.LogisticsRouteSummary,
            service.ConsignmentDetails,
            orderedStages.Count == 0 ? 0 : (int)Math.Round(100d * completedCount / orderedStages.Count));

        var actionTitle = requiresAssessorReview
            ? "Assessor review required"
            : action switch
            {
                "advance" => "Stage advanced",
                "block" => "Stage blocked",
                _ => "Stage draft saved"
            };
        var message = requiresAssessorReview
            ? "This Yellow/Red stage requires assessor sign-off before it can advance."
            : stageNote ?? $"Stage status changed to {status}.";
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            service.Id,
            stage.Id,
            actionTitle,
            message,
            userId,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            serviceId = service.Id,
            stageId = stage.Id,
            stageNumber = stage.StageNumber,
            stageStatus = stage.Status.ToString(),
            workflowStatus = service.WorkflowStatus,
            completionPercentage = service.CompletionPercentage,
            requiresAssessorReview,
            message
        });
    }

    [HttpPost("services/{serviceIdOrCode}/stages/{stageId:long}/documents")]
    [RequestSizeLimit(MaximumStageDocumentBytes + 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public Task<IActionResult> UploadWorkspaceStageDocument(
        string serviceIdOrCode,
        long stageId,
        [FromForm] UploadCaseExecutorStageDocumentRequest request,
        CancellationToken cancellationToken) =>
        SaveWorkspaceStageDocumentAsync(serviceIdOrCode, stageId, request, cancellationToken);

    [HttpPost("services/{serviceIdOrCode}/documents")]
    [RequestSizeLimit(MaximumStageDocumentBytes + 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadWorkspaceStageDocumentWithFormStageId(
        string serviceIdOrCode,
        [FromForm] UploadCaseExecutorStageDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.StageId.HasValue || request.StageId.Value <= 0)
            return BadRequest("stageId is required.");
        return await SaveWorkspaceStageDocumentAsync(
            serviceIdOrCode,
            request.StageId.Value,
            request,
            cancellationToken);
    }

    [HttpGet("services/{serviceIdOrCode}/stages/{stageId:long}/documents/{documentId:long}")]
    public async Task<IActionResult> DownloadWorkspaceStageDocument(
        string serviceIdOrCode,
        long stageId,
        long documentId,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (!await _context.ServiceStageExecutions.AnyAsync(stage =>
                stage.Id == stageId &&
                stage.ShipmentId == service!.Id &&
                stage.RecordStatus == RecordStatus.Active,
                cancellationToken))
            return NotFound("Assigned service stage not found.");

        var serviceDocument = await _context.ServiceDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(document =>
                document.Id == documentId &&
                document.ShipmentId == service!.Id &&
                document.ShipmentStageId == stageId &&
                document.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (serviceDocument is not null)
        {
            var path = ResolveDocumentPath(serviceDocument.FilePath);
            return path is null
                ? NotFound("Document file was not found on the server.")
                : PhysicalFile(path, serviceDocument.MimeType, serviceDocument.OriginalFileName);
        }

        var stageDocument = await _context.StageDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(document =>
                document.Id == documentId &&
                document.ServiceStageId == stageId &&
                document.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (stageDocument is null)
            return NotFound("Assigned service document not found.");
        var stagePath = ResolveDocumentPath(stageDocument.FilePath);
        return stagePath is null
            ? NotFound("Document file was not found on the server.")
            : PhysicalFile(stagePath, stageDocument.MimeType, stageDocument.OriginalFileName);
    }

    [HttpGet("services/{serviceIdOrCode}/stages/{stageId:long}/discussions")]
    public async Task<IActionResult> GetWorkspaceDiscussions(
        string serviceIdOrCode,
        long stageId,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (!await StageBelongsToAssignedServiceAsync(service!.Id, stageId, cancellationToken))
            return NotFound("Assigned service stage not found.");

        var discussions = await _context.StageDiscussions
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == service.Id &&
                item.StageId == stageId &&
                item.RecordStatus == RecordStatus.Active)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.AuthorUserId,
                item.AuthorName,
                item.AuthorRole,
                item.Message,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(discussions);
    }

    [HttpPost("services/{serviceIdOrCode}/stages/{stageId:long}/discussions")]
    public async Task<IActionResult> PostWorkspaceDiscussion(
        string serviceIdOrCode,
        long stageId,
        [FromBody] CaseExecutorDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Trim().Length > 4000)
            return BadRequest("message is required and must be 4,000 characters or fewer.");
        if (!await StageBelongsToAssignedServiceAsync(service!.Id, stageId, cancellationToken))
            return NotFound("Assigned service stage not found.");

        var actorName = await GetExecutorNameAsync(userId, cancellationToken);
        var discussion = new StageDiscussion
        {
            ServiceId = service.Id,
            StageId = stageId,
            AuthorUserId = userId,
            AuthorName = actorName,
            AuthorRole = "Case Executor",
            Message = request.Message.Trim(),
            CreatedAt = DateTime.UtcNow,
            RecordStatus = RecordStatus.Active
        };
        _context.StageDiscussions.Add(discussion);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            service.Id,
            stageId,
            "Stage discussion added",
            discussion.Message,
            userId,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetWorkspaceDiscussions),
            new { serviceIdOrCode, stageId },
            new
            {
                discussion.Id,
                discussion.AuthorUserId,
                discussion.AuthorName,
                discussion.AuthorRole,
                discussion.Message,
                discussion.CreatedAt
            });
    }

    [HttpGet("services/{serviceIdOrCode}/documents")]
    public async Task<IActionResult> GetWorkspaceDocuments(
        string serviceIdOrCode,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;

        var response = await GetAssignedDocumentRowsAsync(service!.Id, cancellationToken);
        return Ok(response);
    }

    [HttpGet("services/{serviceIdOrCode}/risk-compliance")]
    public async Task<IActionResult> GetWorkspaceRiskCompliance(
        string serviceIdOrCode,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        var channel = ResolveRiskChannel(service!.ManagerRiskChannel, service.RiskLevel);
        return Ok(new CaseExecutorRiskComplianceResponse(channel, RiskGuidance(channel)));
    }

    [HttpGet("services/{serviceIdOrCode}/activity")]
    public async Task<IActionResult> GetWorkspaceActivity(
        string serviceIdOrCode,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        var activityRows = await _context.ServiceActivityLogs
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == service!.Id &&
                item.RecordStatus == RecordStatus.Active)
            .OrderByDescending(item => item.CreatedAt)
            .Take(100)
            .Select(item => new
            {
                item.Id,
                item.ActionTitle,
                item.Description,
                item.ActorName,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var activity = activityRows.Select(item => new CaseExecutorActivityItem(
            item.Id,
            item.ActionTitle,
            HumanizeActivityText(item.Description),
            item.ActorName,
            item.CreatedAt)).ToList();
        return Ok(activity);
    }

    [HttpGet("ServiceCategories")]
    public async Task<IActionResult> GetServiceCategories(CancellationToken cancellationToken)
    {
        var (userId, error) = await RequireCaseExecutorAsync(cancellationToken);
        if (error is not null)
            return error;
        return Ok(new[]
        {
            new { code = ServiceType.Unimodal.ToString(), name = "Unimodal", totalStages = 3 },
            new { code = ServiceType.Multimodal.ToString(), name = "Multimodal", totalStages = 12 }
        });
    }

    [HttpPost("services/{serviceIdOrCode}/escalate")]
    public async Task<IActionResult> EscalateWorkspaceService(
        string serviceIdOrCode,
        [FromBody] CaseExecutorEscalationRequest request,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("reason is required.");

        var priority = string.IsNullOrWhiteSpace(request.Priority) ? "High" : request.Priority.Trim();
        var reason = request.Reason.Trim();
        var actorName = await GetExecutorNameAsync(userId, cancellationToken);
        var managerIds = await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"))
            .Select(userRole => userRole.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        _context.ServiceMessages.Add(ServiceMessage.Create(
            "Case executor escalation",
            reason,
            MessageType.System,
            service!.Id,
            userId,
            isUrgent: true,
            priority: priority));
        foreach (var managerId in managerIds)
        {
            _context.Notifications.Add(Notification.Create(
                "Service escalation",
                reason,
                NotificationType.SystemAlert,
                managerId,
                service.Id,
                isUrgent: true));
        }
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            service.Id,
            null,
            "Service escalated",
            $"{priority}: {reason}",
            userId,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { serviceId = service.Id, priority, notifiedManagerCount = managerIds.Count });
    }

    [HttpPost("services/{serviceIdOrCode}/notify-importer")]
    public Task<IActionResult> NotifyWorkspaceImporter(
        string serviceIdOrCode,
        [FromBody] CaseExecutorImporterMessageRequest request,
        CancellationToken cancellationToken) =>
        SendImporterMessageAsync(
            serviceIdOrCode,
            request,
            "Importer notified",
            "Service update",
            NotificationType.ServiceUpdate,
            sendEmail: true,
            cancellationToken);

    [HttpPost("services/{serviceIdOrCode}/message-importer")]
    public Task<IActionResult> MessageWorkspaceImporter(
        string serviceIdOrCode,
        [FromBody] CaseExecutorImporterMessageRequest request,
        CancellationToken cancellationToken) =>
        SendImporterMessageAsync(
            serviceIdOrCode,
            request,
            "Message sent to importer",
            "Service message",
            NotificationType.Message,
            sendEmail: false,
            cancellationToken);

    private async Task<IActionResult> SaveWorkspaceStageDocumentAsync(
        string serviceIdOrCode,
        long stageId,
        UploadCaseExecutorStageDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (request.File is null || request.File.Length == 0)
            return BadRequest("A non-empty document file is required.");
        if (request.File.Length > MaximumStageDocumentBytes)
            return BadRequest("File is too large. Maximum size is 25 MB.");
        if (string.IsNullOrWhiteSpace(request.DocumentType))
            return BadRequest("documentType is required.");

        var extension = Path.GetExtension(Path.GetFileName(request.File.FileName)).ToLowerInvariant();
        var mimeType = extension switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => null
        };
        if (mimeType is null)
            return BadRequest("Only PDF, JPG, PNG, and XLSX files are supported.");
        if (!await HasExpectedSignatureAsync(request.File, extension, cancellationToken))
            return BadRequest("The uploaded file content does not match its file type.");

        var stage = await _context.ServiceStageExecutions.FirstOrDefaultAsync(item =>
            item.Id == stageId &&
            item.ShipmentId == service!.Id &&
            item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (stage is null)
            return NotFound("Assigned service stage not found.");

        var actorName = await GetExecutorNameAsync(userId, cancellationToken);
        var originalFileName = Path.GetFileName(request.File.FileName);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var directory = Path.Combine(_environment.ContentRootPath, "App_Data", "CaseExecutorDocuments");
        Directory.CreateDirectory(directory);
        var fullPath = Path.Combine(directory, fileName);
        await using (var stream = new FileStream(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await request.File.CopyToAsync(stream, cancellationToken);
        }

        var relativePath = Path.Combine("App_Data", "CaseExecutorDocuments", fileName).Replace('\\', '/');
        var documentType = ParseDocumentType(request.DocumentType);
        var document = ServiceDocument.Create(
            fileName,
            relativePath,
            originalFileName,
            extension,
            request.File.Length,
            mimeType,
            documentType,
            service!.Id,
            userId,
            stageId,
            documentTypeLabel: request.DocumentType.Trim());
        _context.ServiceDocuments.Add(document);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            service.Id,
            stageId,
            "Document uploaded",
            $"{request.DocumentType.Trim()} uploaded: {originalFileName}",
            userId,
            actorName));
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            System.IO.File.Delete(fullPath);
            throw;
        }

        return CreatedAtAction(
            nameof(DownloadWorkspaceStageDocument),
            new { serviceIdOrCode, stageId, documentId = document.Id },
            new
            {
                document.Id,
                document.OriginalFileName,
                DocumentType = request.DocumentType.Trim(),
                document.MimeType
            });
    }

    private async Task<IActionResult> SendImporterMessageAsync(
        string serviceIdOrCode,
        CaseExecutorImporterMessageRequest request,
        string actionTitle,
        string defaultSubject,
        NotificationType notificationType,
        bool sendEmail,
        CancellationToken cancellationToken)
    {
        var (userId, service, error) = await RequireAssignedServiceAsync(serviceIdOrCode, cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("message is required.");

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? defaultSubject : request.Subject.Trim();
        var message = request.Message.Trim();
        var actorName = await GetExecutorNameAsync(userId, cancellationToken);
        _context.ServiceMessages.Add(ServiceMessage.Create(
            subject,
            message,
            MessageType.Direct,
            service!.Id,
            userId,
            service.ImporterId));
        var notification = Notification.Create(
            subject,
            message,
            notificationType,
            service.ImporterId,
            service.Id);
        _context.Notifications.Add(notification);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            service.Id,
            null,
            actionTitle,
            $"{subject}: {message}",
            userId,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        var emailSent = false;
        if (sendEmail)
        {
            var emailAddresses = await _context.Customers
                .AsNoTracking()
                .Where(customer =>
                    customer.UserId == service.ImporterId &&
                    customer.RecordStatus == RecordStatus.Active)
                .Select(customer => customer.ContactEmail)
                .Concat(_context.Users
                    .Where(importer => importer.Id == service.ImporterId)
                    .Select(importer => importer.Email))
                .Where(address => address != string.Empty)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            if (emailAddresses.Length > 0)
            {
                var encodedMessage = WebUtility.HtmlEncode(message)
                    .Replace("\r\n", "<br>", StringComparison.Ordinal)
                    .Replace("\n", "<br>", StringComparison.Ordinal);
                await _serviceProvider.GetRequiredService<EmailSenderService>().SendEmailAsync(
                    $"<p>{encodedMessage}</p>",
                    subject,
                    emailAddresses,
                    cancellationToken: cancellationToken);
                notification.MarkAsSent();
                await _context.SaveChangesAsync(cancellationToken);
                emailSent = true;
            }
        }

        return Ok(new { serviceId = service.Id, importerId = service.ImporterId, subject, emailSent });
    }

    private async Task<List<CaseExecutorDocumentResponse>> GetAssignedDocumentRowsAsync(
        long serviceId,
        CancellationToken cancellationToken)
    {
        var stageRows = await _context.StageDocuments
            .AsNoTracking()
            .Where(document =>
                document.RecordStatus == RecordStatus.Active &&
                document.ServiceStageId.HasValue &&
                _context.ServiceStageExecutions.Any(stage =>
                    stage.Id == document.ServiceStageId.Value &&
                    stage.ShipmentId == serviceId &&
                    stage.RecordStatus == RecordStatus.Active))
            .Select(document => new CaseExecutorDocumentResponse
            {
                Id = document.Id,
                Source = "stage",
                ServiceId = serviceId,
                ServiceNumber = document.ServiceStageId.HasValue
                    ? _context.ServiceStageExecutions
                        .Where(stage => stage.Id == document.ServiceStageId.Value)
                        .Select(stage => stage.Shipment!.TrackingNumber)
                        .FirstOrDefault() ?? string.Empty
                    : string.Empty,
                StageId = document.ServiceStageId,
                StageName = document.ServiceStageId.HasValue
                    ? _context.ServiceStageExecutions
                        .Where(stage => stage.Id == document.ServiceStageId.Value)
                        .Select(stage => stage.Stage)
                        .FirstOrDefault().ToString()
                    : "Stage unavailable",
                OriginalFileName = document.OriginalFileName,
                DocumentType = document.DocumentType,
                Description = document.Description,
                UploadedBy = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName,
                UploadedAt = document.CreateAt,
                MimeType = document.MimeType,
                IsVerified = document.IsVerified
            })
            .ToListAsync(cancellationToken);
        var serviceRows = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.ShipmentId == serviceId &&
                document.RecordStatus == RecordStatus.Active)
            .Select(document => new CaseExecutorDocumentResponse
            {
                Id = document.Id,
                Source = "service",
                ServiceId = serviceId,
                ServiceNumber = document.Shipment!.TrackingNumber,
                StageId = document.ShipmentStageId,
                StageName = document.ShipmentStageId.HasValue
                    ? _context.ServiceStageExecutions
                        .Where(stage => stage.Id == document.ShipmentStageId.Value)
                        .Select(stage => stage.Stage)
                        .FirstOrDefault().ToString()
                    : "Service document",
                OriginalFileName = document.OriginalFileName,
                DocumentType = document.DocumentType,
                Description = document.Description,
                UploadedBy = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName,
                UploadedAt = document.CreateAt,
                MimeType = document.MimeType,
                IsVerified = document.IsVerified
            })
            .ToListAsync(cancellationToken);
        return stageRows
            .Concat(serviceRows)
            .OrderByDescending(document => document.UploadedAt)
            .ToList();
    }

    private async Task<(long UserId, Shipment? Service, IActionResult? Error)> RequireAssignedServiceAsync(
        string serviceIdOrCode,
        CancellationToken cancellationToken)
    {
        var (userId, error) = await RequireCaseExecutorAsync(cancellationToken);
        if (error is not null)
            return (0, null, error);
        var service = await FindServiceAsync(serviceIdOrCode, cancellationToken);
        if (service is null || service.RecordStatus != RecordStatus.Active)
            return (0, null, NotFound("Assigned service not found."));
        if (service.AssignedCaseExecutorId != userId)
            return (0, null, Forbid());
        return (userId, service, null);
    }

    private async Task<(long UserId, IActionResult? Error)> RequireCaseExecutorAsync(
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (!userId.HasValue)
            return (0, Unauthorized("User not authenticated."));
        if (!await IsCaseExecutor(userId.Value, cancellationToken))
            return (0, Forbid());
        return (userId.Value, null);
    }

    private long? GetAuthenticatedUserId()
    {
        if (User.Identity?.IsAuthenticated != true)
            return null;

        var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("id")
            ?? User.FindFirstValue("sub");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) && userId > 0
            ? userId
            : null;
    }

    private async Task<Shipment?> FindServiceAsync(
        string serviceIdOrCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceIdOrCode))
            return null;
        var value = serviceIdOrCode.Trim();
        var query = _context.Shipments
            .Where(item => item.RecordStatus == RecordStatus.Active);
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            var serviceById = await query.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (serviceById is not null)
                return serviceById;
        }
        var normalized = value.ToLowerInvariant();
        return await query.FirstOrDefaultAsync(item =>
            item.ServiceCode.ToLower() == normalized ||
            (item.ServiceCode == string.Empty && item.TrackingNumber.ToLower() == normalized),
            cancellationToken);
    }

    private Task<bool> StageBelongsToAssignedServiceAsync(
        long serviceId,
        long stageId,
        CancellationToken cancellationToken) =>
        _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == stageId &&
            stage.ShipmentId == serviceId &&
            stage.RecordStatus == RecordStatus.Active,
            cancellationToken);

    private async Task<string> GetExecutorNameAsync(long userId, CancellationToken cancellationToken) =>
        await _context.Users
            .Where(user => user.Id == userId)
            .Select(user => (user.FirstName + " " + user.LastName).Trim())
            .FirstOrDefaultAsync(cancellationToken) ?? "Case Executor";

    private IQueryable<Shipment> ApplyCaseExecutorFilters(
        IQueryable<Shipment> query,
        CaseExecutorServiceFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            var customerCodeTerm = term.StartsWith("cus-", StringComparison.Ordinal)
                ? term[4..]
                : string.Empty;
            var hasCustomerId = long.TryParse(customerCodeTerm, out var customerId);
            query = query.Where(shipment =>
                shipment.ServiceCode.ToLower().Contains(term) ||
                shipment.TrackingNumber.ToLower().Contains(term) ||
                shipment.ReferenceNumber.ToLower().Contains(term) ||
                shipment.Title.ToLower().Contains(term) ||
                shipment.Description.ToLower().Contains(term) ||
                shipment.Importer.Username.ToLower().Contains(term) ||
                (shipment.Importer.FirstName + " " + shipment.Importer.LastName).ToLower().Contains(term) ||
                shipment.Importer.FirstName.ToLower().Contains(term) ||
                shipment.Importer.LastName.ToLower().Contains(term) ||
                _context.Customers.Any(customer =>
                    customer.UserId == shipment.ImporterId &&
                    customer.RecordStatus == RecordStatus.Active &&
                    (customer.BusinessName.ToLower().Contains(term) ||
                     (hasCustomerId && customer.Id == customerId))) ||
                (hasCustomerId &&
                 shipment.ImporterId == customerId &&
                 !_context.Customers.Any(customer =>
                     customer.UserId == shipment.ImporterId &&
                     customer.RecordStatus == RecordStatus.Active)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = NormalizeFilterToken(filter.Status);
            query = status switch
            {
                "pending" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) == "pending" ||
                    (shipment.WorkflowStatus == string.Empty &&
                     (shipment.Status == ShipmentStatus.Submitted || shipment.Status == ShipmentStatus.Draft))),
                "inprogress" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) == "inprogress" ||
                    (shipment.WorkflowStatus == string.Empty &&
                     (shipment.Status == ShipmentStatus.InProgress || shipment.Status == ShipmentStatus.Approved))),
                "needsreview" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) == "needsreview" ||
                    _context.ServiceStageExecutions.Any(stage =>
                        stage.ShipmentId == shipment.Id &&
                        stage.RecordStatus == RecordStatus.Active &&
                        stage.Status == StageStatus.NeedsReview)),
                "requirescustomeraction" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) == "requirescustomeraction" ||
                    _context.ServiceStageExecutions.Any(stage =>
                        stage.ShipmentId == shipment.Id &&
                        stage.RecordStatus == RecordStatus.Active &&
                        stage.Status == StageStatus.RequiresCustomerAction)),
                "blocked" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) == "blocked" ||
                    _context.ServiceStageExecutions.Any(stage =>
                        stage.ShipmentId == shipment.Id &&
                        stage.RecordStatus == RecordStatus.Active &&
                        (stage.Status == StageStatus.Blocked || stage.IsBlocked))),
                "completed" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) == "completed" ||
                    (shipment.WorkflowStatus == string.Empty && shipment.Status == ShipmentStatus.Completed)),
                _ => query
            };
        }

        if (!string.IsNullOrWhiteSpace(filter.Mode))
        {
            query = filter.Mode.Equals("Unimodal", StringComparison.OrdinalIgnoreCase)
                ? query.Where(shipment =>
                    shipment.ServiceType == ServiceType.Unimodal ||
                    (!shipment.ServiceType.HasValue && shipment.Mode == TransportMode.UniModalRoad))
                : query.Where(shipment =>
                    shipment.ServiceType == ServiceType.Multimodal ||
                    (!shipment.ServiceType.HasValue && shipment.Mode == TransportMode.MultiModalSeaRail));
        }

        if (!string.IsNullOrWhiteSpace(filter.RiskChannel))
        {
            var channel = filter.RiskChannel.Trim().ToLowerInvariant();
            query = query.Where(shipment =>
                shipment.ManagerRiskChannel.ToLower() == channel ||
                (shipment.ManagerRiskChannel == string.Empty &&
                 shipment.RiskLevel.HasValue &&
                 shipment.RiskLevel.Value.ToString().ToLower() == channel));
        }
        return query;
    }

    private static IQueryable<Shipment> ApplyCaseExecutorSort(
        IQueryable<Shipment> query,
        CaseExecutorServiceFilter filter)
    {
        var ascending = filter.SortOrder.Equals("asc", StringComparison.OrdinalIgnoreCase);
        return filter.SortBy.ToLowerInvariant() switch
        {
            "eta" => ascending
                ? query.OrderBy(shipment => shipment.ETA)
                : query.OrderByDescending(shipment => shipment.ETA),
            "sla" => ascending
                ? query.OrderBy(shipment => shipment.SlaDeadline)
                : query.OrderByDescending(shipment => shipment.SlaDeadline),
            _ => ascending
                ? query.OrderBy(shipment => shipment.UpdatedAt ?? shipment.CreateAt)
                : query.OrderByDescending(shipment => shipment.UpdatedAt ?? shipment.CreateAt)
        };
    }

    private static bool IsValidCaseExecutorFilter(
        CaseExecutorServiceFilter filter,
        out string error,
        bool validatePaging = true)
    {
        if (validatePaging && (filter.Page < 1 || filter.PageSize < 1 || filter.PageSize > 100))
        {
            error = "page must be positive and pageSize must be between 1 and 100.";
            return false;
        }
        if (!new[] { "updatedAt", "eta", "sla" }.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase))
        {
            error = "sortBy must be updatedAt, eta, or sla.";
            return false;
        }
        if (!filter.SortOrder.Equals("asc", StringComparison.OrdinalIgnoreCase) &&
            !filter.SortOrder.Equals("desc", StringComparison.OrdinalIgnoreCase))
        {
            error = "sortOrder must be asc or desc.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(filter.Status) &&
            !new[]
            {
                "Pending", "InProgress", "NeedsReview", "RequiresCustomerAction", "Completed", "Blocked"
            }.Select(NormalizeFilterToken).Contains(NormalizeFilterToken(filter.Status)))
        {
            error = "status must be Pending, InProgress, NeedsReview, RequiresCustomerAction, Completed, or Blocked.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(filter.Mode) &&
            !filter.Mode.Equals("Unimodal", StringComparison.OrdinalIgnoreCase) &&
            !filter.Mode.Equals("Multimodal", StringComparison.OrdinalIgnoreCase))
        {
            error = "mode must be Unimodal or Multimodal.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(filter.RiskChannel) && !IsRiskChannel(filter.RiskChannel))
        {
            error = "riskChannel must be Green, Yellow, Blue, or Red.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private async Task<bool> IsManagerOrAdmin(long userId, CancellationToken cancellationToken) =>
        await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"),
                cancellationToken);

    private static ShipmentStage[] WorkflowStages(ServiceType? serviceType, TransportMode mode) =>
        (serviceType ?? (mode == TransportMode.UniModalRoad ? ServiceType.Unimodal : ServiceType.Multimodal))
            == ServiceType.Unimodal
            ? new[] { ShipmentStage.LocalPermission, ShipmentStage.Arrival, ShipmentStage.Clearance }
            : new[]
            {
                ShipmentStage.PrepaymentInvoice,
                ShipmentStage.TransitPermission,
                ShipmentStage.Amendment,
                ShipmentStage.DropRisk,
                ShipmentStage.DeliveryOrder,
                ShipmentStage.WarehouseStatus,
                ShipmentStage.Inspection,
                ShipmentStage.AssessmentandTaxPayment,
                ShipmentStage.Emergency,
                ShipmentStage.ExitandStoragePayment,
                ShipmentStage.Transportation,
                ShipmentStage.Clearance
            };

    private static string ResolveMode(ServiceType? serviceType, TransportMode mode) =>
        (serviceType ?? (mode == TransportMode.UniModalRoad ? ServiceType.Unimodal : ServiceType.Multimodal)).ToString();

    private static string ResolveServiceStatus(string? workflowStatus, ShipmentStatus status) =>
        string.IsNullOrWhiteSpace(workflowStatus)
            ? status switch
            {
                ShipmentStatus.Completed => "Completed",
                ShipmentStatus.Submitted or ShipmentStatus.Draft => "Pending",
                ShipmentStatus.UnderReview => "NeedsReview",
                _ => "InProgress"
            }
            : workflowStatus;

    private static string ResolveAggregateStatus(
        IReadOnlyCollection<ServiceStageExecution> stages,
        int completedCount) =>
        stages.Any(stage => stage.Status == StageStatus.Blocked || stage.IsBlocked)
            ? "Blocked"
            : stages.Any(stage => stage.Status == StageStatus.RequiresCustomerAction || stage.RequiresCustomerAction)
                ? "RequiresCustomerAction"
                : stages.Any(stage => stage.Status == StageStatus.NeedsReview)
                    ? "NeedsReview"
                    : stages.Count > 0 && completedCount == stages.Count
                        ? "Completed"
                        : "InProgress";

    private static string ResolveRiskChannel(string? channel, RiskLevel? riskLevel) =>
        string.IsNullOrWhiteSpace(channel) ? riskLevel?.ToString() ?? "Unassigned" : channel;

    private static string CustomerCode(long customerId) => $"CUS-{customerId:D6}";

    private static string NormalizeFilterToken(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

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

    private static string FormatStageStatus(StageStatus status) =>
        status == StageStatus.Pending ? StageStatus.NotStarted.ToString() : status.ToString();

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
            DocumentType.TransportLicense => "Transport License",
            DocumentType.BankReceipt => "Payment Receipt",
            _ => documentType.ToString()
        };
    }

    private static DocumentType ParseDocumentType(string documentType)
    {
        if (Enum.TryParse<DocumentType>(documentType, true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        return documentType.Trim().ToLowerInvariant() switch
        {
            "air waybill" or "airwaybill" => DocumentType.ArrivalPhoto,
            "commercial invoice" => DocumentType.Invoice,
            "delivery order" => DocumentType.DeliveryOrder,
            "inspection report" => DocumentType.InspectionReport,
            "clearance certificate" or "clearance request" => DocumentType.ClearanceDocument,
            "import permit" or "transit permit" or "amendment request" => DocumentType.LegalDocument,
            _ => DocumentType.Other
        };
    }

    private static string RiskGuidance(string channel) => channel.ToLowerInvariant() switch
    {
        "green" => "Low risk: continue standard processing and retain the supporting documents.",
        "yellow" => "Moderate risk: review declarations and supporting documents before advancing.",
        "blue" => "Document review required: verify the submitted records and record any discrepancy.",
        "red" => "High risk: pause progression, document the concern, and escalate for review.",
        _ => "Follow the applicable compliance review guidance."
    };

    private static double? DurationHours(DateTime? startedAt, DateTime? completedAt) =>
        startedAt.HasValue
            ? Math.Max(0, ((completedAt ?? DateTime.UtcNow) - startedAt.Value).TotalHours)
            : null;

    private static string FullName(string? firstName, string? lastName) =>
        string.Join(" ", new[] { firstName, lastName }.Where(name => !string.IsNullOrWhiteSpace(name))).Trim();

    private static string HumanizeActivityText(string description) =>
        description
            .Replace("RequiresCustomerAction", "Requires customer action", StringComparison.Ordinal)
            .Replace("NeedsReview", "Needs review", StringComparison.Ordinal)
            .Replace("InProgress", "In progress", StringComparison.Ordinal)
            .Replace("NotStarted", "Not started", StringComparison.Ordinal);

    private static string ValueOr(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value;

    private static bool IsRiskChannel(string? value) =>
        value is not null &&
        (value.Equals("Green", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("Yellow", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("Blue", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("Red", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseStageStatus(string? value, out StageStatus status)
    {
        if (!int.TryParse(value, out _) &&
            Enum.TryParse(value, true, out status) &&
            Enum.IsDefined(status) &&
            status != StageStatus.Pending)
            return true;
        status = StageStatus.NotStarted;
        return false;
    }

    private static StageStatus ParseStageStatus(string? value) =>
        TryParseStageStatus(value, out var status) ? status : StageStatus.NotStarted;

    private static string CsvEscape(string value)
    {
        var safeValue = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? $"'{value}"
            : value;
        return $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static async Task<bool> HasExpectedSignatureAsync(
        IFormFile file,
        string extension,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await using var stream = file.OpenReadStream();
        var bytesRead = await stream.ReadAsync(header.AsMemory(), cancellationToken);
        return extension switch
        {
            ".pdf" => bytesRead >= 4 &&
                      header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46,
            ".png" => bytesRead >= 8 &&
                      header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => bytesRead >= 3 &&
                                 header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".xlsx" => bytesRead >= 2 && header[0] == 0x50 && header[1] == 0x4B,
            _ => false
        };
    }

    private sealed record CaseExecutorDocumentRow(
        long Id,
        long StageId,
        string OriginalFileName,
        string DocumentType,
        string MimeType,
        long FileSizeBytes,
        DateTime CreatedAt,
        string UploadedByName);
}
