using Mapster;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using MohamedTransit.Domain.Common;
using MohamedTransit.API.Controllers;
using MohamedTransit.API.Helpers;
using MohamedTransit.Application.Commands;
//using MohamedTransit.Application.Commands.Document; // Commands.Document namespace
using MohamedTransit.Application.Documents.Commands;
using MohamedTransit.Application.Queries.Document;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace MohamedTransit.API.Controllers;

// 1. ለ Controller Request የሚያገለግል DTO class
public class UploadServiceDocumentRequest
{
    public long ServiceId { get; set; }
    public IFormFile File { get; set; } = null!;
    public DocumentType DocumentType { get; set; }
    public long? serviceStageId { get; set; }
    public string? Description { get; set; }
}

[ApiController]
[Route("api/v1/[controller]")]
public class DocumentController : BaseController
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ApplicationDbContext _context;

    public DocumentController(
        IHttpContextAccessor httpContextAccessor,
        ApplicationDbContext context)
    {
        _httpContextAccessor = httpContextAccessor;
        _context = context;
    }

    [HttpPost("UploadServiceDocument")]
    public async Task<IActionResult> UploadServiceDocument(
        [FromForm] UploadServiceDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var userId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (userId == null)
            return Unauthorized("User not authenticated");

        if (await IsCaseExecutorAsync(userId.Value, cancellationToken))
        {
            var assignedService = await _context.Shipments.AnyAsync(shipment =>
                shipment.Id == request.ServiceId &&
                shipment.AssignedCaseExecutorId == userId.Value,
                cancellationToken);
            if (!assignedService)
                return NotFound("Assigned service not found.");

            if (request.serviceStageId.HasValue)
            {
                var assignedStage = await _context.ServiceStageExecutions.AnyAsync(stage =>
                    stage.Id == request.serviceStageId.Value &&
                    stage.ShipmentId == request.ServiceId &&
                    stage.RecordStatus == RecordStatus.Active,
                    cancellationToken);
                if (!assignedStage)
                    return NotFound("Assigned service stage not found.");
            }
        }

        var command = new UploadShipmentDocumentCommand
        {
            ShipmentId = request.ServiceId,
            File = request.File,
            DocumentType = request.DocumentType,
            ServiceStageId = request.serviceStageId,
            Description = request.Description
        };

        var result = await _mediator.Send(command, cancellationToken);
        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(result.Payload);
    }

    [HttpGet("GetServiceDocuments")]
    public async Task<IActionResult> GetServiceDocuments([FromQuery] long serviceId)
    {
        var query = new GetServiceDocumentsQuery { ServiceId = serviceId };
        var result = await _mediator.Send(query);

        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(result.Payload);
    }

    [HttpDelete("DeleteDocument")]
    public async Task<IActionResult> DeleteDocument([FromQuery] long documentId)
    {
        var command = new DeleteDocumentQuery { DocumentId = documentId };
        var result = await _mediator.Send(command);

        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(new { Message = "Document deleted successfully" });
    }

    [HttpPost("VerifyDocument")]
    public async Task<IActionResult> VerifyDocument([FromBody] VerifyDocumentCommand command)
    {
        var userId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (userId == null)
            return Unauthorized("User not authenticated");

        command.VerifiedByUserId = userId.Value;

        var result = await _mediator.Send(command);
        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(result.Payload);
    }

    [HttpGet("DownloadDocument")]
    public async Task<IActionResult> DownloadDocument(
        [FromQuery] long documentId,
        CancellationToken cancellationToken)
    {
        var userId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (userId == null)
            return Unauthorized("User not authenticated");

        if (await IsCaseExecutorAsync(userId.Value, cancellationToken))
        {
            var assignedDocument = await _context.ServiceDocuments
                .AsNoTracking()
                .FirstOrDefaultAsync(document =>
                    document.Id == documentId &&
                    document.RecordStatus == RecordStatus.Active &&
                    document.Shipment != null &&
                    document.Shipment.AssignedCaseExecutorId == userId.Value,
                    cancellationToken);
            if (assignedDocument == null)
                return NotFound("Assigned service document not found.");

            var physicalPath = ResolveDocumentPath(assignedDocument.FilePath);
            if (physicalPath == null)
                return NotFound("Document file was not found on the server.");

            var bytes = await System.IO.File.ReadAllBytesAsync(physicalPath, cancellationToken);
            return File(bytes, assignedDocument.MimeType, assignedDocument.OriginalFileName);
        }

        var query = new DownloadDocumentQuery
        {
            DocumentId = documentId,
        };

        var result = await _mediator.Send(query, cancellationToken);

        return result.IsError ? HandleErrorResponse(result.Errors) : HandleSuccessResponse(result.Payload);
    }

    [HttpPost("DownloadDocuments")]
    public async Task<IActionResult> DownloadDocuments(
        [FromBody] List<long> documentIds,
        CancellationToken cancellationToken)
    {
        if (documentIds == null || documentIds.Count == 0 || documentIds.Any(id => id <= 0))
            return BadRequest("Select valid documents to download.");

        var userId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (userId == null)
            return Unauthorized("User not authenticated");

        var distinctDocumentIds = documentIds.Distinct().ToList();
        if (await IsCaseExecutorAsync(userId.Value, cancellationToken))
        {
            var assignedDocumentsCount = await _context.ServiceDocuments.CountAsync(document =>
                distinctDocumentIds.Contains(document.Id) &&
                document.RecordStatus == RecordStatus.Active &&
                document.Shipment != null &&
                document.Shipment.AssignedCaseExecutorId == userId.Value,
                cancellationToken);
            if (assignedDocumentsCount != distinctDocumentIds.Count)
                return NotFound("One or more assigned service documents were not found.");
        }

        var query = new DownloadMultipleDocumentsQuery
        {
            DocumentIds = distinctDocumentIds
        };

        var result = await _mediator.Send(query, cancellationToken);

        if (result.IsError || result.Payload == null)
            return HandleErrorResponse(result.Errors);

        return File(result.Payload, "application/zip", "case-executor-documents.zip");
    }

    private async Task<bool> IsCaseExecutorAsync(long userId, CancellationToken cancellationToken)
    {
        return await _context.Users
            .Where(user => user.Id == userId)
            .SelectMany(user => user.UserRoles)
            .AnyAsync(userRole => userRole.Role.Name == "CaseExecutor", cancellationToken);
    }

    private static string? ResolveDocumentPath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
            return System.IO.File.Exists(filePath) ? filePath : null;

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath),
            Path.Combine(Directory.GetCurrentDirectory(), filePath)
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }
}
