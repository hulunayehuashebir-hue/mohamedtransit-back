using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MohamedTransit.API.DTO.Manager;
using MohamedTransit.API.Helpers;
using MohamedTransit.Application.DataSeeder;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.API.Controllers;

[ApiController]
[Authorize(Policy = "ManagerAccess")]
[Route("api/v1/Manager/services")]
public sealed class ManagerServicesController : ControllerBase
{
    private const long MaximumDocumentBytes = 25 * 1024 * 1024;
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IWebHostEnvironment _environment;
    private readonly IServiceWorkflowInitializer _workflowInitializer;

    public ManagerServicesController(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IWebHostEnvironment environment,
        IServiceWorkflowInitializer workflowInitializer)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _environment = environment;
        _workflowInitializer = workflowInitializer;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ManagerServiceListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetServices(
        [FromQuery] ManagerServiceFilter filter,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!IsValidFilter(filter, out var validationError))
            return BadRequest(validationError);

        var query = ApplyFilters(_context.Shipments
            .AsNoTracking()
            .Where(shipment => shipment.RecordStatus == RecordStatus.Active), filter);
        var totalCount = await query.CountAsync(cancellationToken);
        query = ApplySort(query, filter);

        var rows = await query
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
                shipment.AssignedCaseExecutor,
                shipment.ETA,
                shipment.SlaDeadline,
                shipment.UpdatedAt,
                shipment.CreateAt,
                Customer = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new
                    {
                        customer.Id,
                        customer.BusinessName
                    })
                    .FirstOrDefault(),
                ImporterName = shipment.Importer.FirstName + " " + shipment.Importer.LastName
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var items = rows.Select(row => new ManagerServiceListItem(
            row.Id,
            ValueOr(row.ServiceCode, row.TrackingNumber),
            ValueOr(row.ReferenceNumber, row.TrackingNumber),
            ValueOr(row.Title, row.Description),
            row.ImporterId,
            ValueOr(row.Customer?.BusinessName, row.ImporterName),
            CustomerCode(row.Customer?.Id ?? row.ImporterId),
            ResolveMode(row.ServiceType, row.Mode),
            ResolveStatus(row.WorkflowStatus, row.Status),
            ResolveRiskChannel(row.ManagerRiskChannel, row.RiskLevel),
            row.AssignedCaseExecutor is null
                ? null
                : FullName(row.AssignedCaseExecutor.FirstName, row.AssignedCaseExecutor.LastName),
            row.ETA,
            row.SlaDeadline.HasValue ? (int)Math.Ceiling((row.SlaDeadline.Value - now).TotalDays) : null,
            row.UpdatedAt ?? row.CreateAt))
            .ToList();

        return Ok(new ManagerServiceListResponse(totalCount, filter.Page, filter.PageSize, items));
    }

    [HttpGet("export")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportServices(
        [FromQuery] ManagerServiceFilter filter,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!IsValidFilter(filter, out var validationError, validatePaging: false))
            return BadRequest(validationError);

        var query = ApplySort(ApplyFilters(_context.Shipments
            .AsNoTracking()
            .Where(shipment => shipment.RecordStatus == RecordStatus.Active), filter), filter);
        var rows = await query
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
                shipment.AssignedCaseExecutor,
                shipment.ETA,
                shipment.SlaDeadline,
                shipment.UpdatedAt,
                shipment.CreateAt,
                Customer = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new
                    {
                        customer.Id,
                        customer.BusinessName
                    })
                    .FirstOrDefault(),
                ImporterName = shipment.Importer.FirstName + " " + shipment.Importer.LastName
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var csv = new StringBuilder("id,serviceCode,referenceNumber,title,importerId,importerName,customerCode,mode,status,riskChannel,caseExecutorName,eta,slaDaysLeft,updatedAt\r\n");
        foreach (var row in rows)
        {
            var values = new[]
            {
                row.Id.ToString(CultureInfo.InvariantCulture),
                ValueOr(row.ServiceCode, row.TrackingNumber),
                ValueOr(row.ReferenceNumber, row.TrackingNumber),
                ValueOr(row.Title, row.Description),
                row.ImporterId.ToString(CultureInfo.InvariantCulture),
                ValueOr(row.Customer?.BusinessName, row.ImporterName),
                CustomerCode(row.Customer?.Id ?? row.ImporterId),
                ResolveMode(row.ServiceType, row.Mode),
                ResolveStatus(row.WorkflowStatus, row.Status),
                ResolveRiskChannel(row.ManagerRiskChannel, row.RiskLevel),
                row.AssignedCaseExecutor is null ? string.Empty : FullName(row.AssignedCaseExecutor.FirstName, row.AssignedCaseExecutor.LastName),
                row.ETA?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.SlaDeadline.HasValue ? Math.Ceiling((row.SlaDeadline.Value - now).TotalDays).ToString(CultureInfo.InvariantCulture) : string.Empty,
                (row.UpdatedAt ?? row.CreateAt).ToString("O", CultureInfo.InvariantCulture)
            };
            csv.AppendLine(string.Join(",", values.Select(CsvEscape)));
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "manager-services.csv");
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(ManagerServiceWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkspace(long id, CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (id <= 0)
            return BadRequest("A valid service ID is required.");

        await _workflowInitializer.EnsureInitializedAsync(id, cancellationToken);
        var service = await _context.Shipments
            .AsNoTracking()
            .Include(shipment => shipment.Importer)
            .Include(shipment => shipment.AssignedCaseExecutor)
            .Include(shipment => shipment.AssignedAssessor)
            .FirstOrDefaultAsync(shipment =>
                shipment.Id == id && shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (service is null)
            return NotFound();

        var customer = await _context.Customers
            .AsNoTracking()
            .Where(item => item.UserId == service.ImporterId && item.RecordStatus == RecordStatus.Active)
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

        var stageRows = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Where(stage => stage.ShipmentId == id && stage.RecordStatus == RecordStatus.Active)
            .Select(stage => new
            {
                Stage = stage,
                RequiredDocuments = stage.RequiredDocuments
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .Select(document => new { document.Id, document.DocumentType })
                    .ToList()
            })
            .ToListAsync(cancellationToken);
        var expectedStageOrder = WorkflowStages(service.ServiceType);
        stageRows = stageRows
            .Where(row => expectedStageOrder.Contains(row.Stage.Stage))
            .OrderBy(row => Array.IndexOf(expectedStageOrder, row.Stage.Stage))
            .ToList();

        var stageIds = stageRows.Select(row => row.Stage.Id).ToArray();
        var stageDocumentRows = await _context.StageDocuments
            .AsNoTracking()
            .Where(document =>
                document.ServiceStageId.HasValue &&
                stageIds.Contains(document.ServiceStageId.Value) &&
                document.RecordStatus == RecordStatus.Active)
            .Select(document => new
            {
                document.Id,
                StageId = document.ServiceStageId!.Value,
                document.FileName,
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
        var serviceDocumentRows = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.ShipmentStageId.HasValue &&
                stageIds.Contains(document.ShipmentStageId.Value) &&
                document.RecordStatus == RecordStatus.Active)
            .Select(document => new
            {
                document.Id,
                StageId = document.ShipmentStageId!.Value,
                document.FileName,
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
        var documentRows = stageDocumentRows
            .Select(document => new ManagerDocumentRow(
                document.Id,
                document.StageId,
                document.FileName,
                document.OriginalFileName,
                ValueOr(document.DocumentTypeLabel, DocumentTypeName(document.DocumentType)),
                document.MimeType,
                document.FileSizeBytes,
                document.CreateAt,
                document.UploadedByName))
            .Concat(serviceDocumentRows.Select(document => new ManagerDocumentRow(
                document.Id,
                document.StageId,
                document.FileName,
                document.OriginalFileName,
                ValueOr(document.DocumentTypeLabel, DocumentTypeName(document.DocumentType)),
                document.MimeType,
                document.FileSizeBytes,
                document.CreateAt,
                document.UploadedByName)))
            .ToList();
        var discussionCounts = await _context.StageDiscussions
            .AsNoTracking()
            .Where(discussion =>
                discussion.ServiceId == id &&
                discussion.RecordStatus == RecordStatus.Active &&
                stageIds.Contains(discussion.StageId))
            .GroupBy(discussion => discussion.StageId)
            .Select(group => new { StageId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.StageId, group => group.Count, cancellationToken);

        var workflowStages = stageRows.Select((row, index) => new
        {
            row.Stage,
            StageNumber = row.Stage.StageNumber > 0 ? row.Stage.StageNumber : index + 1,
            row.RequiredDocuments
        }).ToList();
        var workspaceStages = workflowStages.Select((row, index) =>
        {
            var stage = row.Stage;
            var documents = documentRows.Where(document => document.StageId == stage.Id).ToList();
            var requiredDocuments = row.RequiredDocuments.Select(required =>
            {
                var matchingDocument = documents.FirstOrDefault(document =>
                    string.Equals(
                        document.DocumentType,
                        required.DocumentType,
                        StringComparison.OrdinalIgnoreCase));
                return new ManagerRequiredDocumentResponse(
                    required.DocumentType,
                    matchingDocument is null ? "Outstanding" : "Received",
                    matchingDocument?.Id);
            }).ToList();

            var previous = index == 0 ? null : workflowStages[index - 1].Stage;
            return new ManagerStageResponse(
                stage.Id,
                stage.Id,
                stage.Id,
                row.StageNumber,
                StageName(stage.Stage),
                FormatStageStatus(stage.Status),
                ValueOr(stage.RiskChannel, "Green"),
                stage.StageNote ?? stage.Comments,
                stage.CreateAt,
                stage.UpdatedAt,
                stage.StartedAt,
                stage.CompletedAt,
                DurationHours(stage.StartedAt, stage.CompletedAt),
                stage.CompletedByName,
                previous is null
                    ? null
                    : new ManagerPreviousStageSummaryResponse(
                        StageName(previous.Stage),
                        FormatStageStatus(previous.Status),
                        previous.SummaryNote,
                        previous.CompletedAt,
                        DurationHours(previous.StartedAt, previous.CompletedAt),
                        previous.CompletedByName),
                requiredDocuments,
                documents.Select(document => new ManagerUploadedDocumentResponse(
                    document.Id,
                    document.OriginalFileName,
                    document.DocumentType,
                    document.MimeType,
                    document.FileSizeBytes,
                    document.CreatedAt,
                    document.UploadedByName)).ToList(),
                discussionCounts.GetValueOrDefault(stage.Id));
        }).ToList();

        var activity = await _context.ServiceActivityLogs
            .AsNoTracking()
            .Where(item => item.ServiceId == id && item.RecordStatus == RecordStatus.Active)
            .OrderByDescending(item => item.CreatedAt)
            .Take(25)
            .Select(item => new ManagerActivityResponse(
                item.Id,
                item.ActionTitle,
                item.Description,
                item.ActorName,
                item.CreatedAt))
            .ToListAsync(cancellationToken);

        var staff = new List<ManagerAssignedStaffResponse>();
        if (service.AssignedCaseExecutor is not null && service.AssignedCaseExecutorId.HasValue)
            staff.Add(new ManagerAssignedStaffResponse(
                service.AssignedCaseExecutorId.Value,
                FullName(service.AssignedCaseExecutor.FirstName, service.AssignedCaseExecutor.LastName),
                "Case Executor"));
        if (service.AssignedAssessor is not null && service.AssignedAssessorId.HasValue)
            staff.Add(new ManagerAssignedStaffResponse(
                service.AssignedAssessorId.Value,
                FullName(service.AssignedAssessor.FirstName, service.AssignedAssessor.LastName),
                "KYC / Valuation Assessor"));

        var currentStage = workspaceStages.FirstOrDefault(stage => stage.Status != StageStatus.Completed.ToString());
        var completedCount = workspaceStages.Count(stage => stage.Status == StageStatus.Completed.ToString());
        var completion = workspaceStages.Count == 0
            ? service.CompletionPercentage
            : (int)Math.Round(100d * completedCount / workspaceStages.Count);
        var riskChannel = ResolveRiskChannel(service.ManagerRiskChannel, service.RiskLevel);
        var importerName = FullName(service.Importer.FirstName, service.Importer.LastName);

        return Ok(new ManagerServiceWorkspaceResponse(
            service.Id,
            ValueOr(service.ServiceCode, service.TrackingNumber),
            ValueOr(service.ReferenceNumber, service.TrackingNumber),
            ValueOr(service.Title, service.Description),
            ResolveStatus(service.WorkflowStatus, service.Status),
            riskChannel,
            ResolveMode(service.ServiceType, service.Mode),
            service.Origin,
            service.Destination,
            service.ETA,
            currentStage?.StageNumber ?? workspaceStages.Count,
            workspaceStages.Count,
            completion,
            new ManagerImporterResponse(
                service.ImporterId,
                ValueOr(customer?.BusinessName, importerName),
                CustomerCode(customer?.Id ?? service.ImporterId),
                customer?.TINNumber ?? string.Empty,
                ValueOr(customer?.ContactPerson, importerName),
                ValueOr(customer?.ContactPhone, service.Importer.Phone),
                ValueOr(customer?.ContactEmail, service.Importer.Email)),
            staff,
            new ManagerRiskComplianceResponse(riskChannel, RiskGuidance(riskChannel)),
            activity,
            new ManagerLogisticsResponse(
                service.CarrierOrVessel,
                service.RouteCategory,
                service.ETA,
                service.CarrierOrVessel,
                service.LogisticsRouteSummary,
                service.Origin,
                service.Destination,
                service.ConsignmentDetails),
            workspaceStages));
    }

    [HttpPost("{serviceId:long}/stages/{stageId:long}/update")]
    public async Task<IActionResult> UpdateStage(
        long serviceId,
        long stageId,
        [FromBody] ManagerUpdateStageRequest request,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!IsRiskChannel(request.RiskChannel))
            return BadRequest("riskChannel must be Green, Yellow, Blue, or Red.");
        var action = request.Action?.Trim().ToLowerInvariant();
        if (action is not ("advance" or "draft" or "block"))
            return BadRequest("action must be advance, draft, or block.");
        if (action != "advance" && !TryParseStageStatus(request.StageStatus, out _))
            return BadRequest("stageStatus must be NotStarted, InProgress, NeedsReview, RequiresCustomerAction, Completed, or Blocked.");
        if (action == "block" && string.IsNullOrWhiteSpace(request.StageNote))
            return BadRequest("A reason in stageNote is required when blocking a stage.");

        var stage = await _context.ServiceStageExecutions
            .FirstOrDefaultAsync(item =>
                item.Id == stageId &&
                item.ShipmentId == serviceId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        var service = await _context.Shipments
            .FirstOrDefaultAsync(item =>
                item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (stage is null || service is null)
            return NotFound("Service stage not found.");

        var actor = await GetActorAsync(managerId, cancellationToken);
        var orderedStages = await _context.ServiceStageExecutions
            .Where(item => item.ShipmentId == serviceId && item.RecordStatus == RecordStatus.Active)
            .OrderBy(item => item.StageNumber)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var stageIndex = orderedStages.FindIndex(item => item.Id == stageId);
        var next = stageIndex >= 0 && stageIndex + 1 < orderedStages.Count
            ? orderedStages[stageIndex + 1]
            : null;

        var newStatus = action switch
        {
            "advance" => StageStatus.Completed,
            "block" => StageStatus.Blocked,
            _ => ParseStageStatus(request.StageStatus)
        };
        var summary = action == "advance"
            ? request.StageNote?.Trim()
            : stage.SummaryNote;
        stage.UpdateManagerDetails(
            stage.StageNumber > 0 ? stage.StageNumber : stageIndex + 1,
            newStatus,
            request.RiskChannel.Trim(),
            request.StageNote?.Trim(),
            summary,
            managerId,
            actor.Name);

        if (action == "advance" && next is not null && next.Status != StageStatus.Completed)
        {
            next.UpdateManagerDetails(
                next.StageNumber > 0 ? next.StageNumber : stageIndex + 2,
                StageStatus.InProgress,
                next.RiskChannel,
                next.StageNote,
                next.SummaryNote,
                managerId);
        }

        var completedStages = orderedStages.Count(item => item.Status == StageStatus.Completed);
        var completion = orderedStages.Count == 0
            ? 0
            : (int)Math.Round(100d * completedStages / orderedStages.Count);
        var serviceStatus = action == "block"
            ? "Blocked"
            : orderedStages.Any(item => item.Status == StageStatus.Blocked)
                ? "Blocked"
                : orderedStages.Any(item => item.Status == StageStatus.RequiresCustomerAction)
                    ? "RequiresCustomerAction"
                    : orderedStages.Any(item => item.Status == StageStatus.NeedsReview)
                        ? "NeedsReview"
                        : orderedStages.Count > 0 && completedStages == orderedStages.Count
                            ? "Completed"
                            : "InProgress";
        service.UpdateManagerWorkspace(
            ValueOr(service.ServiceCode, service.TrackingNumber),
            ValueOr(service.ReferenceNumber, service.TrackingNumber),
            ValueOr(service.Title, service.Description),
            serviceStatus,
            request.RiskChannel.Trim(),
            service.ETA,
            service.SlaDeadline,
            service.CarrierOrVessel,
            service.LogisticsRouteSummary,
            service.ConsignmentDetails,
            completion);

        var actionTitle = action switch
        {
            "advance" => "Stage advanced",
            "block" => "Stage blocked",
            _ => "Stage draft saved"
        };
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            stageId,
            actionTitle,
            request.StageNote?.Trim() ?? $"Stage status changed to {newStatus}.",
            managerId,
            actor.Name));
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            serviceId,
            stageId,
            stage.StageNumber,
            StageStatus = stage.Status.ToString(),
            service.WorkflowStatus,
            service.CompletionPercentage
        });
    }

    [HttpPost("{serviceId:long}/stages/{stageId:long}/documents")]
    [RequestSizeLimit(MaximumDocumentBytes + 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadStageDocument(
        long serviceId,
        long stageId,
        [FromForm] IFormFile file,
        [FromForm] string documentType,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty document file is required.");
        if (file.Length > MaximumDocumentBytes)
            return BadRequest("File is too large. Maximum size is 25 MB.");
        if (string.IsNullOrWhiteSpace(documentType))
            return BadRequest("documentType is required.");

        var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
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
        if (!await HasExpectedSignatureAsync(file, extension, cancellationToken))
            return BadRequest("The uploaded file content does not match its file type.");

        var stage = await _context.ServiceStageExecutions
            .FirstOrDefaultAsync(item =>
                item.Id == stageId &&
                item.ShipmentId == serviceId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (stage is null)
            return NotFound("Service stage not found.");
        if (!await _context.Shipments.AnyAsync(item =>
                item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
                cancellationToken))
            return NotFound("Service not found.");

        var actor = await GetActorAsync(managerId, cancellationToken);
        var originalFileName = Path.GetFileName(file.FileName);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var uploadDirectory = Path.Combine(_environment.ContentRootPath, "App_Data", "ManagerDocuments");
        Directory.CreateDirectory(uploadDirectory);
        var fullPath = Path.Combine(uploadDirectory, fileName);
        await using (var stream = new FileStream(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var relativePath = Path.Combine("App_Data", "ManagerDocuments", fileName).Replace('\\', '/');
        var document = ServiceDocument.Create(
            fileName,
            relativePath,
            originalFileName,
            extension,
            file.Length,
            mimeType,
            ParseDocumentType(documentType),
            serviceId,
            managerId,
            serviceStageId: stageId,
            isRequired: false,
            documentTypeLabel: documentType.Trim());
        _context.ServiceDocuments.Add(document);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            stageId,
            "Document uploaded",
            $"{documentType.Trim()} uploaded: {originalFileName}",
            managerId,
            actor.Name));
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
            nameof(DownloadStageDocument),
            new { serviceId, stageId, documentId = document.Id },
            new { document.Id, document.OriginalFileName, DocumentType = documentType.Trim(), document.MimeType });
    }

    [HttpPost("{serviceId:long}/documents")]
    [RequestSizeLimit(MaximumDocumentBytes + 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public Task<IActionResult> UploadStageDocumentWithFormStageId(
        long serviceId,
        [FromForm] long stageId,
        [FromForm] IFormFile file,
        [FromForm] string documentType,
        CancellationToken cancellationToken) =>
        UploadStageDocument(serviceId, stageId, file, documentType, cancellationToken);

    [HttpGet("{serviceId:long}/stages/{stageId:long}/documents/{documentId:long}")]
    public async Task<IActionResult> DownloadStageDocument(
        long serviceId,
        long stageId,
        long documentId,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!await StageBelongsToServiceAsync(serviceId, stageId, cancellationToken))
            return NotFound("Service stage not found.");

        var serviceDocument = await _context.ServiceDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.Id == documentId &&
                item.ShipmentId == serviceId &&
                item.ShipmentStageId == stageId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (serviceDocument is not null)
        {
            var serviceFilePath = ResolveStoredDocumentPath(serviceDocument.FilePath, serviceDocument.FileName);
            return serviceFilePath is null
                ? NotFound("Document file was not found on the server.")
                : PhysicalFile(serviceFilePath, serviceDocument.MimeType, serviceDocument.OriginalFileName);
        }

        var document = await _context.StageDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.Id == documentId &&
                item.ServiceStageId == stageId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (document is null)
            return NotFound("Service document not found.");

        var filePath = ResolveStoredDocumentPath(document.FilePath, document.FileName);
        return filePath is null
            ? NotFound("Document file was not found on the server.")
            : PhysicalFile(filePath, document.MimeType, document.OriginalFileName);
    }

    [HttpGet("/api/v1/Manager/staff")]
    public async Task<IActionResult> GetAvailableStaff(
        [FromQuery] string? role,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!string.IsNullOrWhiteSpace(role) &&
            !role.Equals("CaseExecutor", StringComparison.OrdinalIgnoreCase) &&
            !role.Equals("Assessor", StringComparison.OrdinalIgnoreCase))
            return BadRequest("role must be CaseExecutor or Assessor.");

        var requestedRole = role?.Trim().ToLowerInvariant();
        var staff = await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.User.AccountStatus == AccountStatus.Approved &&
                (requestedRole == null
                    ? userRole.Role.Name.ToLower() == "caseexecutor" ||
                      userRole.Role.Name.ToLower() == "assessor"
                    : userRole.Role.Name.ToLower() == requestedRole))
            .Select(userRole => new ManagerAvailableStaffResponse(
                userRole.UserId,
                (userRole.User.FirstName + " " + userRole.User.LastName).Trim(),
                userRole.Role.Name))
            .ToListAsync(cancellationToken);
        return Ok(staff
            .Distinct()
            .OrderBy(item => item.RoleTitle)
            .ThenBy(item => item.FullName)
            .ToList());
    }

    [HttpPost("{serviceId:long}/assign-executor")]
    public Task<IActionResult> AssignCaseExecutor(
        long serviceId,
        [FromBody] ManagerAssignStaffRequest request,
        CancellationToken cancellationToken) =>
        AssignStaffAsync(serviceId, request.UserId, "caseexecutor", cancellationToken);

    [HttpPost("{serviceId:long}/assign-assessor")]
    public Task<IActionResult> AssignAssessor(
        long serviceId,
        [FromBody] ManagerAssignStaffRequest request,
        CancellationToken cancellationToken) =>
        AssignStaffAsync(serviceId, request.UserId, "assessor", cancellationToken);

    private async Task<IActionResult> AssignStaffAsync(
        long serviceId,
        long staffUserId,
        string role,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (staffUserId <= 0)
            return BadRequest("A valid userId is required.");
        var service = await _context.Shipments.FirstOrDefaultAsync(item =>
            item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service is null)
            return NotFound("Service not found.");

        var staff = await _context.UserRoles
            .Where(userRole =>
                userRole.UserId == staffUserId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.User.AccountStatus == AccountStatus.Approved &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.Role.Name.ToLower() == role)
            .Select(userRole => new
            {
                userRole.UserId,
                FullName = (userRole.User.FirstName + " " + userRole.User.LastName).Trim(),
                userRole.Role.Name
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (staff is null)
            return BadRequest($"The selected user is not an active {role}.");

        if (role == "caseexecutor")
            service.AssignCaseExecutor(staffUserId);
        else
            service.AssignAssessor(staffUserId);

        var actor = await GetActorAsync(managerId, cancellationToken);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            null,
            role == "caseexecutor" ? "Executor assigned" : "Assessor assigned",
            $"{staff.FullName} assigned as {staff.Name}.",
            managerId,
            actor.Name));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new ManagerAssignedStaffResponse(staff.UserId, staff.FullName, staff.Name));
    }

    private string? ResolveStoredDocumentPath(string filePath, string fileName)
    {
        var uploadRoot = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath, "App_Data", "ManagerDocuments"));
        var safeFileName = Path.GetFileName(fileName);
        var managerPath = Path.GetFullPath(Path.Combine(uploadRoot, safeFileName));
        if (!managerPath.StartsWith(uploadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        if (System.IO.File.Exists(managerPath))
            return managerPath;

        var webRoot = _environment.WebRootPath ?? string.Empty;
        var candidates = new[]
        {
            Path.Combine(webRoot, filePath),
            Path.Combine(_environment.ContentRootPath, filePath),
            filePath
        };
        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    [HttpGet("{serviceId:long}/stages/{stageId:long}/discussions")]
    public async Task<IActionResult> GetDiscussions(
        long serviceId,
        long stageId,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (!await StageBelongsToServiceAsync(serviceId, stageId, cancellationToken))
            return NotFound("Service stage not found.");

        var comments = await _context.StageDiscussions
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == serviceId &&
                item.StageId == stageId &&
                item.RecordStatus == RecordStatus.Active)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new ManagerStageDiscussionResponse(
                item.Id,
                item.AuthorUserId,
                item.AuthorName,
                item.AuthorRole,
                item.Message,
                item.CreatedAt))
            .ToListAsync(cancellationToken);
        return Ok(comments);
    }

    [HttpPost("{serviceId:long}/stages/{stageId:long}/discussions")]
    public async Task<IActionResult> PostDiscussion(
        long serviceId,
        long stageId,
        [FromBody] ManagerStageDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Trim().Length > 4000)
            return BadRequest("message is required and must be 4,000 characters or fewer.");
        if (!await StageBelongsToServiceAsync(serviceId, stageId, cancellationToken))
            return NotFound("Service stage not found.");

        var actor = await GetActorAsync(managerId, cancellationToken);
        var discussion = new StageDiscussion
        {
            ServiceId = serviceId,
            StageId = stageId,
            AuthorUserId = managerId,
            AuthorName = actor.Name,
            AuthorRole = actor.Role,
            Message = request.Message.Trim(),
            CreatedAt = DateTime.UtcNow,
            RecordStatus = RecordStatus.Active
        };
        _context.StageDiscussions.Add(discussion);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            stageId,
            "Stage discussion added",
            request.Message.Trim(),
            managerId,
            actor.Name));
        await _context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDiscussions),
            new { serviceId, stageId },
            new ManagerStageDiscussionResponse(
                discussion.Id,
                discussion.AuthorUserId,
                discussion.AuthorName,
                discussion.AuthorRole,
                discussion.Message,
                discussion.CreatedAt));
    }

    [HttpPost("{serviceId:long}/escalate")]
    public async Task<IActionResult> Escalate(
        long serviceId,
        [FromBody] ManagerEscalateRequest request,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("reason is required.");

        var service = await _context.Shipments.FirstOrDefaultAsync(item =>
            item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service is null)
            return NotFound("Service not found.");

        var actor = await GetActorAsync(managerId, cancellationToken);
        var priority = ValueOr(request.Priority, "High");
        var recipients = new[] { service.AssignedCaseExecutorId, service.AssignedAssessorId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var message = request.Reason.Trim();
        var escalation = ServiceMessage.Create(
            "Service escalation",
            message,
            MessageType.System,
            serviceId,
            managerId,
            isUrgent: true,
            priority: priority);
        _context.ServiceMessages.Add(escalation);
        foreach (var recipient in recipients)
        {
            _context.Notifications.Add(Notification.Create(
                "Service escalation",
                message,
                NotificationType.SystemAlert,
                recipient,
                serviceId,
                isUrgent: true));
        }

        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            null,
            "Service escalated",
            $"{priority}: {message}",
            managerId,
            actor.Name));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { serviceId, priority, notifiedStaffCount = recipients.Length });
    }

    [HttpPost("{serviceId:long}/notify-importer")]
    public async Task<IActionResult> NotifyImporter(
        long serviceId,
        [FromBody] ManagerNotifyImporterRequest request,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("message is required.");
        var service = await _context.Shipments.FirstOrDefaultAsync(item =>
            item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service is null)
            return NotFound("Service not found.");

        var actor = await GetActorAsync(managerId, cancellationToken);
        var subject = ValueOr(request.Subject, "Service update");
        var message = request.Message.Trim();
        _context.ServiceMessages.Add(ServiceMessage.Create(
            subject,
            message,
            MessageType.Direct,
            serviceId,
            managerId,
            service.ImporterId));
        _context.Notifications.Add(Notification.Create(
            subject,
            message,
            NotificationType.ServiceUpdate,
            service.ImporterId,
            serviceId));
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            null,
            "Importer notified",
            subject,
            managerId,
            actor.Name));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { serviceId, importerId = service.ImporterId, subject });
    }

    [HttpPost("{serviceId:long}/message-importer")]
    public async Task<IActionResult> MessageImporter(
        long serviceId,
        [FromBody] ManagerMessageImporterRequest request,
        CancellationToken cancellationToken)
    {
        var (managerId, error) = await EnsureManagerAsync(cancellationToken);
        if (error is not null)
            return error;
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("message is required.");
        var service = await _context.Shipments.FirstOrDefaultAsync(item =>
            item.Id == serviceId && item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service is null)
            return NotFound("Service not found.");

        var actor = await GetActorAsync(managerId, cancellationToken);
        var subject = ValueOr(request.Subject, "Service message");
        var message = request.Message.Trim();
        _context.ServiceMessages.Add(ServiceMessage.Create(
            subject,
            message,
            MessageType.Direct,
            serviceId,
            managerId,
            service.ImporterId));
        _context.Notifications.Add(Notification.Create(
            "New message",
            $"You have a new service message: {subject}",
            NotificationType.Message,
            service.ImporterId,
            serviceId));
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            null,
            "Message sent to importer",
            subject,
            managerId,
            actor.Name));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { serviceId, importerId = service.ImporterId, subject });
    }

    private async Task<(long UserId, IActionResult? Error)> EnsureManagerAsync(CancellationToken cancellationToken)
    {
        var userId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (!userId.HasValue || userId.Value <= 0)
            return (0, Unauthorized("User not authenticated."));

        var allowed = await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId.Value &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"),
                cancellationToken);
        return allowed ? (userId.Value, null) : (0, Forbid());
    }

    private IQueryable<Shipment> ApplyFilters(IQueryable<Shipment> query, ManagerServiceFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            var codeTerm = term.StartsWith("cus-", StringComparison.OrdinalIgnoreCase)
                ? term[4..]
                : string.Empty;
            var hasCustomerId = long.TryParse(codeTerm, out var customerId);
            query = query.Where(shipment =>
                shipment.ServiceCode.ToLower().Contains(term) ||
                shipment.TrackingNumber.ToLower().Contains(term) ||
                shipment.ReferenceNumber.ToLower().Contains(term) ||
                shipment.Title.ToLower().Contains(term) ||
                shipment.Description.ToLower().Contains(term) ||
                shipment.Importer.Username.ToLower().Contains(term) ||
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
            var status = filter.Status.Trim();
            query = status.ToLowerInvariant() switch
            {
                "pending" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "pending" ||
                    (shipment.WorkflowStatus == string.Empty &&
                     (shipment.Status == ShipmentStatus.Submitted || shipment.Status == ShipmentStatus.Draft))),
                "inprogress" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "inprogress" ||
                    (shipment.WorkflowStatus == string.Empty &&
                     (shipment.Status == ShipmentStatus.InProgress || shipment.Status == ShipmentStatus.Approved))),
                "needsreview" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "needsreview" ||
                    (shipment.WorkflowStatus == string.Empty && shipment.Status == ShipmentStatus.UnderReview)),
                "completed" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "completed" ||
                    (shipment.WorkflowStatus == string.Empty && shipment.Status == ShipmentStatus.Completed)),
                _ => query.Where(shipment => shipment.WorkflowStatus.ToLower() == status.ToLower())
            };
        }

        if (!string.IsNullOrWhiteSpace(filter.Mode))
        {
            var mode = filter.Mode.Trim();
            if (mode.Equals("Unimodal", StringComparison.OrdinalIgnoreCase))
                query = query.Where(shipment =>
                    shipment.ServiceType == ServiceType.Unimodal ||
                    (!shipment.ServiceType.HasValue && shipment.Mode == TransportMode.UniModalRoad));
            else
                query = query.Where(shipment =>
                    shipment.ServiceType == ServiceType.Multimodal ||
                    (!shipment.ServiceType.HasValue && shipment.Mode == TransportMode.MultiModalSeaRail));
        }

        if (!string.IsNullOrWhiteSpace(filter.RiskChannel))
        {
            var channel = filter.RiskChannel.Trim();
            query = query.Where(shipment =>
                shipment.ManagerRiskChannel.ToLower() == channel.ToLower() ||
                (shipment.ManagerRiskChannel == string.Empty &&
                 shipment.RiskLevel.HasValue &&
                 shipment.RiskLevel.Value.ToString().ToLower() == channel.ToLower()));
        }
        return query;
    }

    private static IQueryable<Shipment> ApplySort(IQueryable<Shipment> query, ManagerServiceFilter filter)
    {
        var ascending = string.Equals(filter.SortOrder, "asc", StringComparison.OrdinalIgnoreCase);
        return (filter.SortBy ?? string.Empty).ToLowerInvariant() switch
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

    private static bool IsValidFilter(
        ManagerServiceFilter filter,
        out string error,
        bool validatePaging = true)
    {
        if (validatePaging && (filter.Page < 1 || filter.PageSize < 1 || filter.PageSize > 100))
        {
            error = "page must be positive and pageSize must be between 1 and 100.";
            return false;
        }
        if (!string.Equals(filter.SortBy, "updatedAt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filter.SortBy, "eta", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filter.SortBy, "sla", StringComparison.OrdinalIgnoreCase))
        {
            error = "sortBy must be updatedAt, eta, or sla.";
            return false;
        }
        if (!string.Equals(filter.SortOrder, "asc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filter.SortOrder, "desc", StringComparison.OrdinalIgnoreCase))
        {
            error = "sortOrder must be asc or desc.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(filter.Status) &&
            !new[] { "Pending", "InProgress", "NeedsReview", "RequiresCustomerAction", "Completed", "Blocked" }
                .Contains(filter.Status, StringComparer.OrdinalIgnoreCase))
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

    private async Task<(string Name, string Role)> GetActorAsync(long userId, CancellationToken cancellationToken)
    {
        var actor = await _context.Users
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                Name = user.FirstName + " " + user.LastName,
                Role = user.UserRoles
                    .Where(userRole => userRole.Role.RecordStatus == RecordStatus.Active)
                    .Select(userRole => userRole.Role.Name)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);
        return actor is null
            ? ("Manager", "Manager")
            : (ValueOr(actor.Name.Trim(), "Manager"), ValueOr(actor.Role, "Manager"));
    }

    private Task<bool> StageBelongsToServiceAsync(long serviceId, long stageId, CancellationToken cancellationToken) =>
        _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == stageId &&
            stage.ShipmentId == serviceId &&
            stage.RecordStatus == RecordStatus.Active,
            cancellationToken);

    private static string ResolveMode(ServiceType? serviceType, TransportMode mode) =>
        (serviceType ?? (mode == TransportMode.UniModalRoad ? ServiceType.Unimodal : ServiceType.Multimodal)).ToString();

    private static string ResolveStatus(string? workflowStatus, ShipmentStatus status) =>
        string.IsNullOrWhiteSpace(workflowStatus) ? LegacyStatus(status) : workflowStatus;

    private static string LegacyStatus(ShipmentStatus status) => status switch
    {
        ShipmentStatus.Completed => "Completed",
        ShipmentStatus.Submitted or ShipmentStatus.Draft => "Pending",
        ShipmentStatus.UnderReview => "NeedsReview",
        _ => "InProgress"
    };

    private static string ResolveRiskChannel(string? channel, RiskLevel? riskLevel) =>
        string.IsNullOrWhiteSpace(channel) ? riskLevel?.ToString() ?? "Unassigned" : channel;

    private static string CustomerCode(long customerId) => $"CUS-{customerId:D6}";

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

    private static string DocumentTypeName(DocumentType documentType) => documentType switch
    {
        DocumentType.Invoice => "Commercial invoice",
        DocumentType.DeliveryOrder => "Delivery Order",
        DocumentType.InspectionReport => "Inspection Report",
        DocumentType.ArrivalPhoto => "Air waybill",
        DocumentType.ClearanceDocument => "Clearance Certificate",
        DocumentType.TransportLicense => "Transport License",
        DocumentType.BankReceipt => "Payment Receipt",
        DocumentType.LegalDocument => "Supporting Document",
        _ => documentType.ToString()
    };

    private static ShipmentStage[] WorkflowStages(ServiceType? serviceType) =>
        serviceType == ServiceType.Unimodal
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

    private static string ValueOr(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value;

    private static bool IsRiskChannel(string? value) =>
        value is not null &&
        (value.Equals("Green", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("Yellow", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("Blue", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("Red", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseStageStatus(string? status, out StageStatus parsed)
    {
        if (!int.TryParse(status, out _) &&
            Enum.TryParse(status, true, out parsed) &&
            Enum.IsDefined(parsed) &&
            parsed != StageStatus.Pending)
            return true;
        parsed = StageStatus.NotStarted;
        return false;
    }

    private static StageStatus ParseStageStatus(string? status) =>
        TryParseStageStatus(status, out var parsed) ? parsed : StageStatus.NotStarted;

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

    private sealed record ManagerDocumentRow(
        long Id,
        long StageId,
        string FileName,
        string OriginalFileName,
        string DocumentType,
        string MimeType,
        long FileSizeBytes,
        DateTime CreatedAt,
        string UploadedByName);
}
