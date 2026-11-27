using Mapster;

using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MohamedTransit.API.DTO.MOT.Response;
using MohamedTransit.API.DTO.Document.Request;
using MohamedTransit.API.Helpers;
using MohamedTransit.Application.Commands;
using MohamedTransit.Application.Queries.Customer.CaseExecuter;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.API.DTO.Document.Response;
using MohamedTransit.Application.DTO;
using MohamedTransit.Domain.Entities;
using MohamedTransit.API.DTO.MOT.Request;
using MohamedTransit.Application.Documents.Commands;
using MohamedTransit.Application.DataSeeder;
namespace MohamedTransit.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public partial class CaseExecutorController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CaseExecutorController(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IWebHostEnvironment environment,
        IServiceWorkflowInitializer workflowInitializer,
        IServiceProvider serviceProvider)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _environment = environment;
        _workflowInitializer = workflowInitializer;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Get assigned services for the case executor
    /// </summary>
    [HttpGet("GetAssignedServices")]
    public async Task<IActionResult> GetAssignedServices([FromQuery] RecordStatus? recordStatus)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();

        var servicesQuery = _context.Shipments
            .AsNoTracking()
            .Where(shipment =>
                shipment.AssignedCaseExecutorId == currentUserId.Value &&
                shipment.RecordStatus == RecordStatus.Active);

        if (recordStatus.HasValue)
        {
            servicesQuery = servicesQuery.Where(shipment => shipment.RecordStatus == recordStatus.Value);
        }

        var services = await servicesQuery
            .OrderByDescending(shipment => shipment.CreateAt)
            .Select(shipment => new ShipmentDetail
            {
                Id = shipment.Id,
                ServiceNumber = shipment.TrackingNumber,
                ItemDescription = shipment.Description,
                RouteCategory = shipment.RouteCategory,
                DeclaredValue = shipment.DeclaredValue,
                TaxCategory = shipment.TaxCategory,
                CountryOfOrigin = shipment.CountryOfOrigin,
                Origin = shipment.Origin,
                Destination = shipment.Destination,
                ServiceType = shipment.ServiceType ?? ServiceType.Multimodal,
                Status = shipment.Status,
                RiskLevel = shipment.RiskLevel,
                AssignedHub = shipment.AssignedHub,
                CustomerId = shipment.ImporterId,
                ImporterName = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => customer.BusinessName)
                    .FirstOrDefault() ?? string.Empty,
                ImporterTinNumber = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => customer.TINNumber)
                    .FirstOrDefault() ?? string.Empty,
                AssignedCaseExecutorId = shipment.AssignedCaseExecutorId,
                AssignedAssessorId = shipment.AssignedAssessorId,
                RegisteredDate = shipment.CreateAt,
                DueAt = shipment.Status == ShipmentStatus.Submitted ||
                        shipment.Status == ShipmentStatus.UnderReview
                    ? shipment.CreateAt.AddHours(48)
                    : null,
                LastUpdateDate = shipment.UpdatedAt,
                RequiresCustomerAction = _context.ServiceStageExecutions.Any(stage =>
                    stage.ShipmentId == shipment.Id &&
                    stage.RecordStatus == RecordStatus.Active &&
                    stage.RequiresCustomerAction),
                IsBlocked = _context.ServiceStageExecutions.Any(stage =>
                    stage.ShipmentId == shipment.Id &&
                    stage.RecordStatus == RecordStatus.Active &&
                    (stage.IsBlocked || stage.Status == StageStatus.Blocked)),
                HasNeedsReview = _context.ServiceStageExecutions.Any(stage =>
                    stage.ShipmentId == shipment.Id &&
                    stage.RecordStatus == RecordStatus.Active &&
                    stage.Status == StageStatus.NeedsReview),
                HasPendingStage = _context.ServiceStageExecutions.Any(stage =>
                    stage.ShipmentId == shipment.Id &&
                    stage.RecordStatus == RecordStatus.Active &&
                    stage.Status == StageStatus.Pending)
            })
            .ToListAsync();

        return HandleSuccessResponse(services);
    }

    /// <summary>
    /// Get stage documents attached to services assigned to the authenticated case executor.
    /// </summary>
    [HttpGet("GetAssignedDocuments")]
    public async Task<IActionResult> GetAssignedDocuments(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();

        var stageDocuments = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Where(stage =>
                stage.ShipmentId.HasValue &&
                stage.RecordStatus == RecordStatus.Active &&
                stage.Shipment != null &&
                stage.Shipment.AssignedCaseExecutorId == currentUserId.Value &&
                stage.Documents.Any(document => document.RecordStatus == RecordStatus.Active))
            .SelectMany(stage => stage.Documents
                .Where(document => document.RecordStatus == RecordStatus.Active)
                .Select(document => new CaseExecutorDocumentResponse
            {
                Id = document.Id,
                Source = "stage",
                ServiceId = stage.ShipmentId!.Value,
                ServiceNumber = stage.Shipment!.TrackingNumber,
                StageId = stage.Id,
                StageName = stage.Stage.ToString(),
                OriginalFileName = document.OriginalFileName,
                DocumentType = document.DocumentType,
                Description = document.Description,
                UploadedBy = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName,
                UploadedByCurrentUser = document.UploadedByUserId == currentUserId.Value,
                UploadedAt = document.CreateAt,
                MimeType = document.MimeType,
                IsVerified = document.IsVerified
            }))
            .OrderByDescending(document => document.UploadedAt)
            .ToListAsync(cancellationToken);

        var serviceDocuments = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.RecordStatus == RecordStatus.Active &&
                document.Shipment != null &&
                document.Shipment.AssignedCaseExecutorId == currentUserId.Value)
            .Select(document => new CaseExecutorDocumentResponse
            {
                Id = document.Id,
                Source = "service",
                ServiceId = document.ShipmentId!.Value,
                ServiceNumber = document.Shipment!.TrackingNumber,
                StageId = document.ShipmentStageId,
                StageName = document.ShipmentStageId.HasValue
                    ? _context.ServiceStageExecutions
                        .Where(stage => stage.Id == document.ShipmentStageId.Value)
                        .Select(stage => stage.Stage.ToString())
                        .FirstOrDefault() ?? "Stage unavailable"
                    : "Service document",
                OriginalFileName = document.OriginalFileName,
                DocumentType = document.DocumentType,
                Description = document.Description,
                UploadedBy = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName,
                UploadedByCurrentUser = document.UploadedByUserId == currentUserId.Value,
                UploadedAt = document.CreateAt,
                MimeType = document.MimeType,
                IsVerified = document.IsVerified
            })
            .ToListAsync(cancellationToken);

        var documents = stageDocuments
            .Concat(serviceDocuments)
            .OrderByDescending(document => document.UploadedAt)
            .ToList();

        return HandleSuccessResponse(documents);
    }

    /// <summary>
    /// Upload a document to a service assigned to the authenticated case executor.
    /// </summary>
    [HttpPost("UploadAssignedServiceDocument")]
    public async Task<IActionResult> UploadAssignedServiceDocument(
        [FromForm] UploadCaseExecutorDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();
        if (request.File == null || request.File.Length == 0)
            return BadRequest("A document file is required.");
        if (request.File.Length > 25 * 1024 * 1024)
            return BadRequest("File is too large. Maximum size is 25 MB.");
        if (!TryParseDocumentType(request.DocumentType, out var documentType))
            return BadRequest(
                $"Invalid document type '{request.DocumentType}'. Supported values: " +
                $"{string.Join(", ", Enum.GetNames<DocumentType>())}, Supporting document, Commercial invoice, Air waybill, Payment receipt, and Clearance certificate.");

        var assignedShipment = await _context.Shipments.AnyAsync(
            shipment => shipment.Id == request.ShipmentId &&
                        shipment.AssignedCaseExecutorId == currentUserId.Value,
            cancellationToken);
        if (!assignedShipment)
            return NotFound("Assigned service not found.");

        if (request.ShipmentStageId.HasValue)
        {
            var assignedStage = await _context.ServiceStageExecutions.AnyAsync(stage =>
                stage.Id == request.ShipmentStageId.Value &&
                stage.ShipmentId == request.ShipmentId &&
                stage.RecordStatus == RecordStatus.Active,
                cancellationToken);
            if (!assignedStage)
                return NotFound("Assigned service stage not found.");
        }

        var result = await _mediator.Send(new UploadShipmentDocumentCommand
        {
            ShipmentId = request.ShipmentId,
            UploadedByUserId = currentUserId.Value,
            File = request.File,
            DocumentType = documentType,
            ServiceStageId = request.ShipmentStageId,
            Description = request.Description
        }, cancellationToken);

        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(result.Payload);
    }

    /// <summary>
    /// Soft-delete a document uploaded by the executor for one of their assigned services.
    /// </summary>
    [HttpDelete("DeleteAssignedDocument")]
    public async Task<IActionResult> DeleteAssignedDocument(
        [FromQuery] long documentId,
        [FromQuery] string source,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();

        if (string.IsNullOrWhiteSpace(source))
            return BadRequest("Document source is required.");

        var normalizedSource = source.Trim().ToLowerInvariant();
        if (normalizedSource == "service")
        {
            var document = await _context.ServiceDocuments.FirstOrDefaultAsync(item =>
                item.Id == documentId &&
                item.RecordStatus == RecordStatus.Active &&
                item.UploadedByUserId == currentUserId.Value &&
                item.Shipment != null &&
                item.Shipment.AssignedCaseExecutorId == currentUserId.Value,
                cancellationToken);
            if (document == null)
                return NotFound("Document uploaded by you for an assigned service was not found.");
            document.RecordStatus = RecordStatus.Delete;
        }
        else if (normalizedSource == "stage")
        {
            var document = await _context.StageDocuments.FirstOrDefaultAsync(item =>
                item.Id == documentId &&
                item.RecordStatus == RecordStatus.Active &&
                item.UploadedByUserId == currentUserId.Value &&
                item.ServiceStageId.HasValue &&
                _context.ServiceStageExecutions.Any(stage =>
                    stage.Id == item.ServiceStageId.Value &&
                    stage.Shipment != null &&
                    stage.Shipment.AssignedCaseExecutorId == currentUserId.Value),
                cancellationToken);
            if (document == null)
                return NotFound("Document uploaded by you for an assigned service was not found.");
            document.RecordStatus = RecordStatus.Delete;
        }
        else
        {
            return BadRequest("Document source must be service or stage.");
        }

        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(new { Message = "Document deleted successfully." });
    }

    /// <summary>
    /// Verify another executor's document for a service assigned to the current executor.
    /// Executors cannot verify documents they uploaded themselves.
    /// </summary>
    [HttpPut("VerifyAssignedDocument")]
    public async Task<IActionResult> VerifyAssignedDocument(
        [FromBody] VerifyAssignedCaseExecutorDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();

        var source = request.Source.Trim().ToLowerInvariant();
        if (source == "service")
        {
            var document = await _context.ServiceDocuments.FirstOrDefaultAsync(item =>
                item.Id == request.DocumentId &&
                item.RecordStatus == RecordStatus.Active &&
                item.Shipment != null &&
                item.Shipment.AssignedCaseExecutorId == currentUserId.Value,
                cancellationToken);
            if (document == null)
                return NotFound("Assigned service document not found.");
            if (document.UploadedByUserId == currentUserId.Value)
                return Forbid();
            document.Verify(currentUserId.Value, request.VerificationNotes);
        }
        else if (source == "stage")
        {
            var document = await _context.StageDocuments.FirstOrDefaultAsync(item =>
                item.Id == request.DocumentId &&
                item.RecordStatus == RecordStatus.Active &&
                item.ServiceStageId.HasValue &&
                _context.ServiceStageExecutions.Any(stage =>
                    stage.Id == item.ServiceStageId.Value &&
                    stage.Shipment != null &&
                    stage.Shipment.AssignedCaseExecutorId == currentUserId.Value),
                cancellationToken);
            if (document == null)
                return NotFound("Assigned service document not found.");
            if (document.UploadedByUserId == currentUserId.Value)
                return Forbid();
            document.Verify(currentUserId.Value, request.VerificationNotes);
        }
        else
        {
            return BadRequest("Document source must be service or stage.");
        }

        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(new { Message = "Document verified successfully." });
    }

    /// <summary>
    /// Download a service or stage document attached to a service assigned to the current executor.
    /// </summary>
    [HttpGet("DownloadAssignedDocument")]
    public async Task<IActionResult> DownloadAssignedDocument(
        [FromQuery] long documentId,
        [FromQuery] string source,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();
        if (string.IsNullOrWhiteSpace(source))
            return BadRequest("Document source is required.");

        var normalizedSource = source.Trim().ToLowerInvariant();
        string? filePath;
        string fileName;
        string mimeType;

        if (normalizedSource == "service")
        {
            var document = await _context.ServiceDocuments
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.Id == documentId &&
                    item.RecordStatus == RecordStatus.Active &&
                    item.Shipment != null &&
                    item.Shipment.AssignedCaseExecutorId == currentUserId.Value,
                    cancellationToken);
            if (document == null)
                return NotFound("Assigned service document not found.");
            filePath = document.FilePath;
            fileName = document.OriginalFileName;
            mimeType = document.MimeType;
        }
        else if (normalizedSource == "stage")
        {
            var document = await _context.StageDocuments
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.Id == documentId &&
                    item.RecordStatus == RecordStatus.Active &&
                    item.ServiceStageId.HasValue &&
                    _context.ServiceStageExecutions.Any(stage =>
                        stage.Id == item.ServiceStageId.Value &&
                        stage.Shipment != null &&
                        stage.Shipment.AssignedCaseExecutorId == currentUserId.Value),
                    cancellationToken);
            if (document == null)
                return NotFound("Assigned service document not found.");
            filePath = document.FilePath;
            fileName = document.OriginalFileName;
            mimeType = document.MimeType;
        }
        else
        {
            return BadRequest("Document source must be service or stage.");
        }

        var physicalPath = ResolveDocumentPath(filePath);
        if (physicalPath == null)
            return NotFound("Document file was not found on the server.");

        var bytes = await System.IO.File.ReadAllBytesAsync(physicalPath, cancellationToken);
        return File(bytes, mimeType, fileName);
    }

    /// <summary>
    /// Download selected service and stage documents as a ZIP archive.
    /// </summary>
    [HttpPost("DownloadAssignedDocuments")]
    public async Task<IActionResult> DownloadAssignedDocuments(
        [FromBody] List<CaseExecutorDocumentReference>? documents,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();
        if (documents == null || documents.Count == 0)
            return BadRequest("Select at least one document to download.");

        if (documents.Any(reference =>
            reference == null ||
            reference.DocumentId <= 0 ||
            string.IsNullOrWhiteSpace(reference.Source)))
            return BadRequest("Document references must have a valid ID and source.");

        var references = documents
            .Select(reference => new CaseExecutorDocumentReference
            {
                DocumentId = reference.DocumentId,
                Source = reference.Source.Trim().ToLowerInvariant()
            })
            .Distinct()
            .ToList();
        if (references.Count == 0 || references.Count != documents.Count)
            return BadRequest("Document references must have a valid ID and source.");
        if (references.Any(reference => reference.Source is not ("service" or "stage")))
            return BadRequest("Document source must be service or stage.");

        var serviceIds = references
            .Where(reference => reference.Source == "service")
            .Select(reference => reference.DocumentId)
            .ToList();
        var stageIds = references
            .Where(reference => reference.Source == "stage")
            .Select(reference => reference.DocumentId)
            .ToList();
        var serviceDocuments = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                serviceIds.Contains(document.Id) &&
                document.RecordStatus == RecordStatus.Active &&
                document.Shipment != null &&
                document.Shipment.AssignedCaseExecutorId == currentUserId.Value)
            .Select(document => new { document.Id, document.FilePath, document.OriginalFileName })
            .ToListAsync(cancellationToken);
        var stageDocuments = await _context.StageDocuments
            .AsNoTracking()
            .Where(document =>
                stageIds.Contains(document.Id) &&
                document.RecordStatus == RecordStatus.Active &&
                document.ServiceStageId.HasValue &&
                _context.ServiceStageExecutions.Any(stage =>
                    stage.Id == document.ServiceStageId.Value &&
                    stage.Shipment != null &&
                    stage.Shipment.AssignedCaseExecutorId == currentUserId.Value))
            .Select(document => new { document.Id, document.FilePath, document.OriginalFileName })
            .ToListAsync(cancellationToken);

        if (serviceDocuments.Count != serviceIds.Count || stageDocuments.Count != stageIds.Count)
            return NotFound("One or more assigned service documents were not found.");

        await using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = serviceDocuments
                .Select(document => (Source: "service", document.Id, document.FilePath, document.OriginalFileName))
                .Concat(stageDocuments
                    .Select(document => (Source: "stage", document.Id, document.FilePath, document.OriginalFileName)));
            foreach (var document in entries)
            {
                var physicalPath = ResolveDocumentPath(document.FilePath);
                if (physicalPath == null)
                    return NotFound($"Document file '{document.OriginalFileName}' was not found on the server.");

                var entryName = Path.GetFileName(document.OriginalFileName);
                if (string.IsNullOrWhiteSpace(entryName))
                    entryName = $"{document.Source}-{document.Id}";
                if (archive.GetEntry(entryName) != null)
                    entryName = $"{document.Source}-{document.Id}-{entryName}";

                var entry = archive.CreateEntry(entryName);
                await using var entryStream = entry.Open();
                await using var fileStream = System.IO.File.OpenRead(physicalPath);
                await fileStream.CopyToAsync(entryStream, cancellationToken);
            }
        }

        return File(archiveStream.ToArray(), "application/zip", "case-executor-documents.zip");
    }

    /// <summary>
    /// Update a stage status on a service assigned to the authenticated case executor.
    /// </summary>
    [HttpPut("UpdateAssignedStageStatus")]
    public async Task<IActionResult> UpdateAssignedStageStatus(
        [FromBody] UpdateCaseExecutorStageStatusRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();

        if (!Enum.IsDefined(request.Status) || request.Status == StageStatus.Blocked)
            return BadRequest("Choose a valid stage status. Use BlockStage to block a stage.");

        var stage = await _context.ServiceStageExecutions
            .Include(execution => execution.Shipment)
            .FirstOrDefaultAsync(execution =>
                execution.Id == request.StageId &&
                execution.ShipmentId == request.ShipmentId &&
                execution.Shipment != null &&
                execution.Shipment.AssignedCaseExecutorId == currentUserId.Value,
                cancellationToken);

        if (stage == null)
            return NotFound("Assigned service stage not found.");

        var result = await _mediator.Send(new MohamedTransit.Application.UpdateServiceStageCommand
        {
            ShipmentId = request.ShipmentId,
            ShipmentStageId = request.StageId,
            Status = request.Status,
            Notes = request.Notes ?? stage.Comments,
            UpdatedByUserId = currentUserId.Value
        }, cancellationToken);
        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(result.Payload);
    }

    /// <summary>
    /// Get service details for execution
    /// </summary>
    [HttpGet("GetAssignedServiceById")]
    public async Task<IActionResult> GetAssignedServiceById([FromQuery] long Id, CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (!await _context.Shipments.AnyAsync(shipment =>
                shipment.Id == Id &&
                shipment.AssignedCaseExecutorId == currentUserId.Value &&
                shipment.RecordStatus == RecordStatus.Active,
                cancellationToken))
            return NotFound("Assigned service not found.");

        var query = new GetCaseExecutorAssignedServicesByIdQuery { Id = Id, AssignedCaseExecutorId = currentUserId.Value };
        var result = await _mediator.Send(query, cancellationToken);
        // var rolesList = result.Payload.Adapt<List<ServiceDetail>>();
        return result.IsError ? HandleErrorResponse(result.Errors) : HandleSuccessResponse(result.Payload);
    }

    /// <summary>
    /// Get recorded actions performed by the authenticated case executor.
    /// </summary>
    [HttpGet("GetMyActivity")]
    public async Task<IActionResult> GetMyActivity(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();

        var userId = currentUserId.Value;
        var activities = new List<CaseExecutorActivityResponse>();

        var comments = await _context.StageComments
            .AsNoTracking()
            .Where(comment =>
                comment.CommentedByUserId == userId &&
                comment.ServiceStage != null &&
                comment.ServiceStage.Shipment != null &&
                comment.ServiceStage.Shipment.AssignedCaseExecutorId == userId)
            .Select(comment => new
            {
                comment.Id,
                comment.Comment,
                comment.CreateAt,
                comment.ServiceStage!.ShipmentId,
                TrackingNumber = comment.ServiceStage.Shipment!.TrackingNumber
            })
            .ToListAsync(cancellationToken);

        activities.AddRange(comments.Select(comment => new CaseExecutorActivityResponse
        {
            Id = comment.Id,
            Action = "Added stage comment",
            Description = comment.Comment,
            Category = "service",
            Reference = comment.TrackingNumber,
            ServiceId = comment.ShipmentId,
            Timestamp = comment.CreateAt
        }));

        var serviceDocuments = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.UploadedByUserId == userId &&
                document.Shipment != null &&
                document.Shipment.AssignedCaseExecutorId == userId)
            .Select(document => new
            {
                document.Id,
                document.OriginalFileName,
                document.Description,
                document.CreateAt,
                document.ShipmentId,
                TrackingNumber = document.Shipment!.TrackingNumber
            })
            .ToListAsync(cancellationToken);

        activities.AddRange(serviceDocuments.Select(document => new CaseExecutorActivityResponse
        {
            Id = document.Id,
            Action = "Uploaded service document",
            Description = string.IsNullOrWhiteSpace(document.Description)
                ? document.OriginalFileName
                : $"{document.OriginalFileName} — {document.Description}",
            Category = "document",
            Reference = document.TrackingNumber,
            ServiceId = document.ShipmentId,
            Timestamp = document.CreateAt
        }));

        var stageDocuments = await _context.StageDocuments
            .AsNoTracking()
            .Where(document =>
                document.UploadedByUserId == userId &&
                document.ServiceStageId.HasValue)
            .Select(document => new
            {
                document.Id,
                document.OriginalFileName,
                document.Description,
                document.CreateAt,
                StageId = document.ServiceStageId!.Value
            })
            .ToListAsync(cancellationToken);

        var stageIds = stageDocuments.Select(document => document.StageId).Distinct().ToArray();
        if (stageIds.Length > 0)
        {
            var stages = await _context.ServiceStageExecutions
                .AsNoTracking()
                .Where(stage =>
                    stageIds.Contains(stage.Id) &&
                    stage.Shipment != null &&
                    stage.Shipment.AssignedCaseExecutorId == userId)
                .Select(stage => new
                {
                    stage.Id,
                    stage.ShipmentId,
                    TrackingNumber = stage.Shipment!.TrackingNumber
                })
                .ToListAsync(cancellationToken);

            var stageServices = stages.ToDictionary(stage => stage.Id);
            activities.AddRange(stageDocuments
                .Where(document => stageServices.ContainsKey(document.StageId))
                .Select(document =>
                {
                    var stage = stageServices[document.StageId];
                    return new CaseExecutorActivityResponse
                    {
                        Id = document.Id,
                        Action = "Uploaded stage document",
                        Description = string.IsNullOrWhiteSpace(document.Description)
                            ? document.OriginalFileName
                            : $"{document.OriginalFileName} — {document.Description}",
                        Category = "document",
                        Reference = stage.TrackingNumber,
                        ServiceId = stage.ShipmentId,
                        Timestamp = document.CreateAt
                    };
                }));
        }

        var blockedStages = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Where(stage =>
                stage.UpdatedByUserId == userId &&
                stage.IsBlocked &&
                stage.Shipment != null &&
                stage.Shipment.AssignedCaseExecutorId == userId)
            .Select(stage => new
            {
                stage.Id,
                stage.BlockReason,
                stage.UpdatedAt,
                stage.CreateAt,
                stage.ShipmentId,
                TrackingNumber = stage.Shipment!.TrackingNumber
            })
            .ToListAsync(cancellationToken);

        activities.AddRange(blockedStages.Select(stage => new CaseExecutorActivityResponse
        {
            Id = stage.Id,
            Action = "Blocked stage",
            Description = stage.BlockReason ?? string.Empty,
            Category = "service",
            Reference = stage.TrackingNumber,
            ServiceId = stage.ShipmentId,
            Timestamp = stage.UpdatedAt ?? stage.CreateAt
        }));

        var orderedActivities = activities
            .OrderByDescending(activity => activity.Timestamp)
            .ThenByDescending(activity => activity.Id)
            .ToList();

        return HandleSuccessResponse(orderedActivities);
    }



    /// <summary>
    /// Upload document for a service stage
    /// </summary>
    [HttpPost("UploadStageDocument")]
    public async Task<IActionResult> UploadStageDocument(
        [FromForm] UploadDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();

        if (request.File == null || request.File.Length == 0)
        {
            return BadRequest(new
            {
                Error = true,
                Message = "Document file is required."
            });
        }

        if (request.File.Length > 25 * 1024 * 1024)
            return BadRequest("File is too large. Maximum size is 25 MB.");

        if (!TryParseDocumentType(request.DocumentType, out var documentType))
            return BadRequest(
                $"Invalid document type '{request.DocumentType}'. Supported values: " +
                $"{string.Join(", ", Enum.GetNames<DocumentType>())}, Supporting document, Commercial invoice, Air waybill, Payment receipt, and Clearance certificate.");

        if (!request.StageId.HasValue || request.StageId.Value <= 0)
            return BadRequest("A valid stage ID is required.");

        var assignedStage = await _context.ServiceStageExecutions
            .AnyAsync(stage =>
                stage.Id == request.StageId.Value &&
                stage.ShipmentId == request.ShipmentId &&
                stage.Shipment != null &&
                stage.Shipment.AssignedCaseExecutorId == currentUserId.Value,
                cancellationToken);

        if (!assignedStage)
            return NotFound("Assigned service stage not found.");

        var command = new UploadDocumentCommand
        {
            ShipmentId = request.ShipmentId,
            StageId = request.StageId.Value,
            File = request.File,
            DocumentType = documentType,
            Description = request.Description,
            UploadedByUserId = currentUserId.Value
        };
        var result = await _mediator.Send(command, cancellationToken);
        var documentDetail = result.Payload?.Adapt<DocumentDetail>();
        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(documentDetail);
    }

    /// <summary>
    /// Add comment to a service stage
    /// </summary>
    [HttpPost("AddStageComment")]
    public async Task<IActionResult> AddStageComment(
        [FromBody] AddStageCommentRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();

        var stage = await _context.ShipmentStages
            .Include(item => item.Shipment)
            .FirstOrDefaultAsync(s =>
                s.Id == request.StageId &&
                s.ShipmentId == request.ShipmentId &&
                s.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (stage == null)
            return NotFound("Service stage not found");
        if (stage.Shipment?.AssignedCaseExecutorId != currentUserId.Value)
            return Forbid();
        if (string.IsNullOrWhiteSpace(request.Comment))
            return BadRequest("A comment is required.");

        var comment = StageComment.Create(
            request.Comment,
            request.StageId,
            currentUserId.Value
        );

        _context.StageComments.Add(comment);
        var actorName = await _context.Users
            .Where(user => user.Id == currentUserId.Value)
            .Select(user => (user.FirstName + " " + user.LastName).Trim())
            .FirstAsync(cancellationToken);
        _context.StageDiscussions.Add(new StageDiscussion
        {
            ServiceId = request.ShipmentId,
            StageId = request.StageId,
            AuthorUserId = currentUserId.Value,
            AuthorName = actorName,
            AuthorRole = "Case Executor",
            Message = request.Comment.Trim(),
            CreatedAt = DateTime.UtcNow,
            RecordStatus = RecordStatus.Active
        });
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            request.ShipmentId,
            request.StageId,
            "Stage discussion added",
            request.Comment.Trim(),
            currentUserId.Value,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(comment);
    }

    /// <summary>
    /// Set risk level for a service
    /// </summary>
    [HttpPut("SetRiskLevel")]
    public async Task<IActionResult> SetRiskLevel(
        [FromBody] SetRiskLevelRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (!Enum.IsDefined(request.RiskLevel))
            return BadRequest("Choose a valid risk level.");

        var shipment = await _context.Shipments
            .FirstOrDefaultAsync(s =>
                s.Id == request.ShipmentId &&
                s.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (shipment == null)
            return NotFound("Shipment not found");
        if (shipment.AssignedCaseExecutorId != currentUserId.Value)
            return Forbid();

        shipment.UpdateRiskLevel(request.RiskLevel);
        var actorName = await GetExecutorNameAsync(currentUserId.Value, cancellationToken);
        shipment.UpdateManagerWorkspace(
            string.IsNullOrWhiteSpace(shipment.ServiceCode) ? shipment.TrackingNumber : shipment.ServiceCode,
            string.IsNullOrWhiteSpace(shipment.ReferenceNumber) ? shipment.TrackingNumber : shipment.ReferenceNumber,
            string.IsNullOrWhiteSpace(shipment.Title) ? shipment.Description : shipment.Title,
            string.IsNullOrWhiteSpace(shipment.WorkflowStatus) ? ResolveServiceStatus(null, shipment.Status) : shipment.WorkflowStatus,
            request.RiskLevel.ToString(),
            shipment.ETA,
            shipment.SlaDeadline,
            shipment.CarrierOrVessel,
            shipment.LogisticsRouteSummary,
            shipment.ConsignmentDetails,
            shipment.CompletionPercentage);

        // Add risk notes to the current stage if provided
        if (!string.IsNullOrEmpty(request.RiskNotes))
        {
            var currentStage = await _context.ShipmentStages
                .Where(s => s.ShipmentId == request.ShipmentId)
                .Where(s => s.RecordStatus == RecordStatus.Active)
                .OrderBy(s => s.StageNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (currentStage != null)
            {
                currentStage.AddRiskNotes(request.RiskNotes);
            }
        }

        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            shipment.Id,
            null,
            "Risk channel updated",
            $"Risk channel set to {request.RiskLevel}. {request.RiskNotes}".Trim(),
            currentUserId.Value,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(shipment);
    }

    /// <summary>
    /// Block a service stage
    /// </summary>
    [HttpPut("BlockStage")]
    public async Task<IActionResult> BlockStage(
        [FromBody] BlockStageRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();

        var stage = await _context.ShipmentStages
            .Include(item => item.Shipment)
            .FirstOrDefaultAsync(s =>
                s.Id == request.StageId &&
                s.ShipmentId == request.ShipmentId &&
                s.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (stage == null)
            return NotFound("Service stage not found");
        if (stage.Shipment?.AssignedCaseExecutorId != currentUserId.Value)
            return Forbid();

        var result = await _mediator.Send(new MohamedTransit.Application.UpdateServiceStageCommand
        {
            ShipmentId = request.ShipmentId,
            ShipmentStageId = request.StageId,
            Status = StageStatus.Blocked,
            Notes = request.Reason,
            UpdatedByUserId = currentUserId.Value
        }, cancellationToken);
        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(result.Payload);
    }

    /// <summary>
    /// Get case executor dashboard
    /// </summary>
    [HttpGet("GetDashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value))
            return Forbid();

        var dashboard = new MohamedTransit.API.DTO.MOT.Response.CaseExecutorDashboardResponse
        {
            AssignedServices = await _context.Shipments.CountAsync(s => s.AssignedCaseExecutorId == currentUserId.Value),
            PendingServices = await _context.Shipments.CountAsync(s => s.AssignedCaseExecutorId == currentUserId.Value && s.Status == ShipmentStatus.InProgress),
            CompletedServices = await _context.Shipments.CountAsync(s => s.AssignedCaseExecutorId == currentUserId.Value && s.Status == ShipmentStatus.Completed),
            BlockedStages = await _context.ShipmentStages.CountAsync(s =>
                s.Shipment != null &&
                s.Shipment.AssignedCaseExecutorId == currentUserId.Value &&
                s.IsBlocked)
        };

        // Get today's tasks
        dashboard.TodaysTasks = await _context.ShipmentStages
            .Include(s => s.Shipment)
            .Where(s => s.Shipment != null &&
                       s.Shipment.AssignedCaseExecutorId == currentUserId.Value &&
                       s.Status == StageStatus.Pending &&
                       s.CreatedDate.Date == DateTime.UtcNow.Date)
            .ToListAsync();

        // Get urgent notifications
        dashboard.UrgentNotifications = await _context.Notifications
            .Include(n => n.Shipment)
            .Where(n => n.UserId == currentUserId.Value && n.IsUrgent && !n.IsRead)
            .ToListAsync();

        return HandleSuccessResponse(dashboard);
    }

    [HttpGet("DownloadStageDocument")]
    public async Task<IActionResult> DownloadStageDocument(
        [FromQuery] long documentId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();

        var isAssigned = await _context.StageDocuments
            .AnyAsync(document =>
                document.Id == documentId &&
                document.ServiceStageId.HasValue &&
                _context.ServiceStageExecutions.Any(stage =>
                    stage.Id == document.ServiceStageId.Value &&
                    stage.Shipment != null &&
                    stage.Shipment.AssignedCaseExecutorId == currentUserId.Value),
                cancellationToken);
        if (!isAssigned)
            return NotFound("Assigned service document not found.");

        var query = new DownloadStageDocumentQuery
        {
            DocumentId = documentId,
        };

        var result = await _mediator.Send(query, cancellationToken);
        if (result.IsError || result.Payload == null)
            return HandleErrorResponse(result.Errors);

        var physicalPath = ResolveDocumentPath(result.Payload.FilePath);
        if (physicalPath == null)
            return NotFound("Document file was not found on the server.");

        var bytes = await System.IO.File.ReadAllBytesAsync(physicalPath, cancellationToken);
        return File(bytes, result.Payload.MimeType, result.Payload.OriginalFileName);
    }
    [HttpPost("DownloadStageDocuments")]
    public async Task<IActionResult> DownloadStageDocuments(
        [FromBody] List<long>? documentIds,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsCaseExecutor(currentUserId.Value, cancellationToken))
            return Forbid();

        if (documentIds == null || documentIds.Count == 0)
            return BadRequest("Select at least one document to download.");

        var distinctIds = documentIds.Distinct().ToList();
        var assignedDocumentsCount = await _context.StageDocuments
            .CountAsync(document =>
                distinctIds.Contains(document.Id) &&
                document.ServiceStageId.HasValue &&
                _context.ServiceStageExecutions.Any(stage =>
                    stage.Id == document.ServiceStageId.Value &&
                    stage.Shipment != null &&
                    stage.Shipment.AssignedCaseExecutorId == currentUserId.Value),
                cancellationToken);

        if (assignedDocumentsCount != distinctIds.Count)
            return NotFound("One or more assigned service documents were not found.");

        var query = new DownloadMultipleStageDocumentsQuery
        {
            DocumentIds = distinctIds
        };

        var result = await _mediator.Send(query, cancellationToken);
        return result.IsError || result.Payload == null
            ? HandleErrorResponse(result.Errors)
            : File(result.Payload, "application/zip", "case-executor-documents.zip");

    }

    private string? ResolveDocumentPath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
            return System.IO.File.Exists(filePath) ? filePath : null;

        var candidates = new[]
        {
            Path.Combine(_environment.ContentRootPath, filePath),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath),
            Path.Combine(Directory.GetCurrentDirectory(), filePath)
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    private static bool TryParseDocumentType(string? raw, out DocumentType parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (Enum.TryParse(value, ignoreCase: true, out parsed) &&
            Enum.IsDefined(typeof(DocumentType), parsed))
            return true;

        var normalizedValue = new string(value.Where(char.IsLetterOrDigit).ToArray())
            .ToUpperInvariant();
        foreach (var documentType in Enum.GetValues<DocumentType>())
        {
            var normalizedName = new string(documentType.ToString()
                .Where(char.IsLetterOrDigit)
                .ToArray());
            if (string.Equals(normalizedName, normalizedValue, StringComparison.OrdinalIgnoreCase))
            {
                parsed = documentType;
                return true;
            }
        }

        var alias = normalizedValue switch
        {
            "SUPPORTINGDOCUMENT" => DocumentType.LegalDocument,
            "COMMERCIALINVOICE" => DocumentType.Invoice,
            "AIRWAYBILL" => DocumentType.ArrivalPhoto,
            "PAYMENTRECEIPT" => DocumentType.BankReceipt,
            "CLEARANCECERTIFICATE" => DocumentType.ClearanceDocument,
            _ => (DocumentType?)null
        };
        if (!alias.HasValue)
            return false;

        parsed = alias.Value;
        return true;
    }

    private async Task<bool> IsCaseExecutor(long userId, CancellationToken cancellationToken = default)
    {
        return await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.User.AccountStatus == AccountStatus.Approved &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.Role.Name.ToLower() == "caseexecutor",
                cancellationToken);
    }
}
