using Mapster;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using MohamedTransit.Application.Handlers.Assessor;
using MohamedTransit.Application.Queries.Customer.CaseExecuter;
using MohamedTransit.API.DTO.NewFolder.Request;
using MohamedTransit.API.DTO.Assessor.Request;
using MohamedTransit.API.Helpers;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Route("api/v1/admin/assessor")]
[Authorize]
public class AssessorController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IWebHostEnvironment _environment;

    public AssessorController(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IWebHostEnvironment environment)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _environment = environment;
    }

    /// <summary>
    /// Get customers pending approval
    /// </summary>
    [HttpGet("CustomerApprovals")]
    [HttpGet("customers/approvals")]
    [HttpGet("GetPendingCustomerApprovals")]
    public async Task<IActionResult> GetPendingCustomerApprovals(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var customers = await _context.Set<Customer>()
            .Include(c => c.Documents)
                .ThenInclude(document => document.VerifiedByUser)
            .Where(c => !c.IsVerified &&
                        c.VerificationStatus != "Rejected" &&
                        c.RecordStatus == RecordStatus.Active)
            .OrderByDescending(c => c.CreateAt)
            .ToListAsync(cancellationToken);

        var importerIds = customers
            .Where(customer => customer.UserId.HasValue)
            .Select(customer => customer.UserId!.Value)
            .Distinct()
            .ToArray();
        var activeServiceCounts = await _context.Set<Shipment>()
            .Where(shipment =>
                importerIds.Contains(shipment.ImporterId) &&
                shipment.RecordStatus == RecordStatus.Active &&
                shipment.Status != ShipmentStatus.Completed &&
                shipment.Status != ShipmentStatus.Rejected &&
                shipment.Status != ShipmentStatus.Cancelled &&
                shipment.Status != ShipmentStatus.Deliverd)
            .GroupBy(shipment => shipment.ImporterId)
            .Select(group => new { ImporterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ImporterId, item => item.Count, cancellationToken);

        var approvalRecords = customers.Select(customer => new
        {
            customer.Id,
            customer.BusinessName,
            customer.TINNumber,
            customer.BusinessLicense,
            customer.BusinessAddress,
            customer.ContactPerson,
            customer.ContactPhone,
            customer.ContactEmail,
            customer.BusinessType,
            customer.ImportLicense,
            customer.ImportLicenseExpiry,
            customer.IsVerified,
            customer.IsReturnedForRevision,
            customer.VerificationStatus,
            customer.VerificationNotes,
            customer.PreviousReviewNote,
            customer.CreateAt,
            ActiveServices = customer.UserId.HasValue &&
                             activeServiceCounts.TryGetValue(customer.UserId.Value, out var count)
                ? count
                : 0,
            Documents = customer.Documents
                .Where(document => document.RecordStatus == RecordStatus.Active)
                .Select(document => new
                {
                    id = document.Id,
                    documentType = CustomerDocumentLabel(document.DocumentType),
                    name = CustomerDocumentLabel(document.DocumentType),
                    fileName = document.OriginalFileName,
                    fileUrl = $"/api/v1/Assessor/DownloadCustomerDocument?documentId={document.Id}",
                    status = document.VerificationStatus,
                    isRequired = document.IsRequired,
                    expiresAt = document.ExpiryDate,
                    isExpired = document.IsExpired,
                    uploadedAt = document.CreateAt,
                    verifiedByName = document.VerifiedByUser == null
                        ? null
                        : FullName(document.VerifiedByUser.FirstName, document.VerifiedByUser.LastName),
                    verifiedAt = document.VerifiedAt,
                    notes = document.VerificationNotes
                })
                .ToList()
        }).ToList();

        return HandleSuccessResponse(approvalRecords);
    }

    [HttpGet("customers/{customerId:long}")]
    public async Task<IActionResult> GetCustomerApprovalDetails(long customerId, CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var customer = await _context.Customers
            .AsNoTracking()
            .Include(item => item.Documents)
                .ThenInclude(document => document.VerifiedByUser)
            .Include(item => item.VerifiedByUser)
            .FirstOrDefaultAsync(item =>
                item.Id == customerId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (customer == null)
            return NotFound("Customer not found.");

        var documents = customer.Documents
            .Where(document => document.RecordStatus == RecordStatus.Active)
            .OrderBy(document => document.DocumentType)
            .Select(document => new
            {
                id = document.Id,
                documentType = CustomerDocumentLabel(document.DocumentType),
                name = CustomerDocumentLabel(document.DocumentType),
                fileName = document.OriginalFileName,
                fileUrl = $"/api/v1/Assessor/DownloadCustomerDocument?documentId={document.Id}",
                status = document.VerificationStatus,
                isRequired = document.IsRequired,
                expiresAt = document.ExpiryDate,
                isExpired = document.IsExpired,
                uploadedAt = document.CreateAt,
                verifiedByName = document.VerifiedByUser == null
                ? null
                : FullName(document.VerifiedByUser.FirstName, document.VerifiedByUser.LastName),
                verifiedAt = document.VerifiedAt,
                notes = document.VerificationNotes
            })
            .ToList();

        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.BusinessName,
            customer.TINNumber,
            customer.BusinessLicense,
            customer.ImportLicense,
            customer.ImportLicenseExpiry,
            customer.BusinessAddress,
            customer.City,
            customer.State,
            customer.PostalCode,
            customer.ContactPerson,
            customer.ContactPhone,
            customer.ContactEmail,
            customer.BusinessType,
            customer.IsVerified,
            customer.VerificationStatus,
            customer.IsReturnedForRevision,
            customer.VerificationNotes,
            customer.PreviousReviewNote,
            documents
        });
    }

    /// <summary>
    /// Approve or reject a customer
    /// </summary>
    [HttpPut("ApproveCustomer")]
    public async Task<IActionResult> ApproveCustomer(
        [FromBody] ApproveCustomerDto request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.CustomerId <= 0)
            return BadRequest("A valid customer ID is required.");

        var customer = await _context.Set<Customer>()
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.RecordStatus == RecordStatus.Active, cancellationToken);

        if (customer == null)
            return NotFound("Customer not found");

        if (request.ReturnForRevision)
        {
            if (request.IsApproved)
                return BadRequest("A customer cannot be approved and returned for revision in the same request.");
            if (string.IsNullOrWhiteSpace(request.Notes))
                return BadRequest("A revision note is required.");

            customer.ReturnForRevision(request.Notes.Trim());
        }
        else if (request.IsApproved)
        {
            var activeDocuments = customer.Documents
                .Where(document => document.RecordStatus == RecordStatus.Active)
                .ToList();
            if (activeDocuments.Count == 0)
                return BadRequest("At least one active KYC document is required before customer approval.");
            if (activeDocuments.Any(document => !document.IsVerified || document.IsExpired))
                return BadRequest("Verify all active customer documents and resolve expired documents before approval.");

            customer.Verify(currentUserId.Value, request.Notes?.Trim());
        }
        else
        {
            customer.RejectVerification(request.Notes);
        }

        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            customer.VerificationStatus,
            "Customer",
            customer.BusinessName,
            customer.VerificationNotes ?? $"Customer assessment status set to {customer.VerificationStatus}.",
            customerId: customer.Id));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(customer);
    }

    [HttpPost("customers/{customerId:long}/decision")]
    public async Task<IActionResult> DecideCustomerAssessment(
        long customerId,
        [FromBody] CustomerAssessmentDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var decision = NormalizeDecision(request.Decision);
        if (decision is not ("Approve" or "Reject" or "RequestChanges"))
            return BadRequest("Decision must be Approve, Reject, or RequestChanges.");
        if (decision is "Reject" or "RequestChanges" && string.IsNullOrWhiteSpace(request.AssessorNote))
            return BadRequest("assessorNote is required when rejecting or requesting changes.");

        var customer = await _context.Customers
            .Include(item => item.Documents)
            .FirstOrDefaultAsync(item =>
                item.Id == customerId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (customer == null)
            return NotFound("Customer not found.");

        var activeDocuments = customer.Documents
            .Where(document => document.RecordStatus == RecordStatus.Active)
            .ToList();
        if (decision == "Approve")
        {
            if (activeDocuments.Count == 0)
                return BadRequest("At least one active KYC document is required before customer approval.");
            if (activeDocuments.Any(document => document.VerificationStatus != "Verified" || document.IsExpired))
                return BadRequest("All active KYC documents must be verified and unexpired before approval.");
            customer.Verify(currentUserId.Value, request.AssessorNote?.Trim());
        }
        else if (decision == "Reject")
        {
            customer.RejectVerification(request.AssessorNote!.Trim());
        }
        else
        {
            customer.ReturnForRevision(request.AssessorNote!.Trim());
        }

        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            decision,
            "Customer",
            customer.BusinessName,
            request.AssessorNote?.Trim() ?? $"Customer assessment decision: {decision}.",
            customerId: customer.Id));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.VerificationStatus,
            customer.IsVerified,
            customer.IsReturnedForRevision,
            customer.VerificationNotes,
            customer.PreviousReviewNote,
            customer.VerifiedAt
        });
    }

    [HttpPut("VerifyCustomerDocument")]
    public async Task<IActionResult> VerifyCustomerDocument(
        [FromBody] VerifyCustomerDocumentDto request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var document = await _context.Set<CustomerDocument>()
            .Include(item => item.Customer)
            .Include(item => item.VerifiedByUser)
            .FirstOrDefaultAsync(item =>
                item.Id == request.DocumentId &&
                item.RecordStatus == RecordStatus.Active &&
                item.Customer != null &&
                item.Customer.RecordStatus == RecordStatus.Active &&
                !item.Customer.IsVerified,
                cancellationToken);

        if (document == null)
            return NotFound("An active customer document was not found.");
        var requestedDecision = string.IsNullOrWhiteSpace(request.Decision)
            ? request.IsVerified ? "Verified" : "Rejected"
            : NormalizeDecision(request.Decision);
        var decision = requestedDecision switch
        {
            "Verified" => "Verified",
            "Reject" or "Rejected" => "Rejected",
            _ => null
        };
        if (decision is not ("Verified" or "Rejected"))
            return BadRequest("Document decision must be Verified or Rejected.");
        if (decision == "Verified" && document.IsExpired)
            return BadRequest("An expired customer document cannot be verified.");

        document.SetReviewStatus(decision, currentUserId.Value, request.Notes?.Trim());
        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            decision,
            "CustomerDocument",
            CustomerDocumentLabel(document.DocumentType),
            request.Notes?.Trim() ?? $"KYC document {decision.ToLowerInvariant()}.",
            customerId: document.CustomerId,
            documentId: document.Id));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            id = document.Id,
            documentType = CustomerDocumentLabel(document.DocumentType),
            status = document.VerificationStatus,
            document.IsVerified,
            document.VerificationNotes,
            expiresAt = document.ExpiryDate,
            isExpired = document.IsExpired,
            document.VerifiedAt
        });
    }

    [HttpPost("customers/{customerId:long}/documents/{docId:long}/verify")]
    public async Task<IActionResult> ReviewCustomerDocument(
        long customerId,
        long docId,
        [FromBody] VerifyCustomerDocumentDto request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var existingDocument = await _context.CustomerDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.Id == docId &&
                item.CustomerId == customerId &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (existingDocument == null)
            return NotFound("An active KYC document for this customer was not found.");

        request.DocumentId = docId;
        if (string.IsNullOrWhiteSpace(request.Decision))
            request.Decision = request.IsVerified ? "Verified" : "Rejected";
        return await VerifyCustomerDocument(request, cancellationToken);
    }

    [HttpGet("DownloadCustomerDocument")]
    public async Task<IActionResult> DownloadCustomerDocument(
        [FromQuery] long documentId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var document = await _context.Set<CustomerDocument>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.Id == documentId &&
                item.RecordStatus == RecordStatus.Active &&
                item.Customer != null &&
                item.Customer.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (document == null)
            return NotFound("An active customer document was not found.");
        var physicalPath = ResolveCustomerDocumentPath(document.FilePath);
        if (physicalPath == null)
            return NotFound("The customer document file was not found on the server.");

        return PhysicalFile(
            physicalPath,
            string.IsNullOrWhiteSpace(document.MimeType) ? "application/octet-stream" : document.MimeType,
            System.IO.Path.GetFileName(document.OriginalFileName));
    }

    [HttpGet("customers/{customerId:long}/documents/{documentId:long}/download")]
    public async Task<IActionResult> DownloadCustomerKycDocument(
        long customerId,
        long documentId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var belongsToCustomer = await _context.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                document.Id == documentId &&
                document.CustomerId == customerId &&
                document.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (!belongsToCustomer)
            return NotFound("An active KYC document for this customer was not found.");

        return await DownloadCustomerDocument(documentId, cancellationToken);
    }

    [HttpGet("Documents")]
    [HttpGet("GetAssessmentDocuments")]
    public async Task<IActionResult> GetAssessmentDocuments(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var serviceDocuments = await _context.Set<ServiceDocument>()
            .AsNoTracking()
            .Where(document =>
                document.RecordStatus == RecordStatus.Active &&
                document.Shipment != null &&
                document.Shipment.RecordStatus == RecordStatus.Active)
            .OrderByDescending(document => document.CreateAt)
            .Include(document => document.Shipment)
            .Include(document => document.UploadedByUser)
            .ToListAsync(cancellationToken);

        var customerDocuments = await _context.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.RecordStatus == RecordStatus.Active &&
                document.Customer != null &&
                document.Customer.RecordStatus == RecordStatus.Active)
            .Include(document => document.Customer)
            .Include(document => document.UploadedByUser)
            .Include(document => document.VerifiedByUser)
            .OrderByDescending(document => document.CreateAt)
            .ToListAsync(cancellationToken);

        var documents = serviceDocuments.Select(document => new AssessmentDocumentRecord(
                document.Id,
                null,
                document.ShipmentId,
                document.Shipment?.TrackingNumber ?? string.Empty,
                document.ShipmentStageId,
                document.DocumentType.ToString(),
                document.OriginalFileName,
                $"/api/v1/Document/DownloadDocument?documentId={document.Id}",
                document.IsVerified ? "Verified" : "Pending",
                null,
                false,
                document.CreateAt,
                null,
                null,
                document.UploadedByUser == null
                    ? null
                    : FullName(document.UploadedByUser.FirstName, document.UploadedByUser.LastName),
                document.IsVerified,
                document.FileSizeBytes,
                document.Description,
                "Service"))
            .Concat(customerDocuments.Select(document => new AssessmentDocumentRecord(
                document.Id,
                document.CustomerId,
                null,
                document.Customer?.BusinessName ?? string.Empty,
                null,
                CustomerDocumentLabel(document.DocumentType),
                document.OriginalFileName,
                $"/api/v1/Assessor/DownloadCustomerDocument?documentId={document.Id}",
                document.VerificationStatus,
                document.ExpiryDate,
                document.IsExpired,
                document.CreateAt,
                document.VerifiedByUser == null
                    ? null
                    : FullName(document.VerifiedByUser.FirstName, document.VerifiedByUser.LastName),
                document.VerifiedAt,
                document.UploadedByUser == null
                    ? null
                    : FullName(document.UploadedByUser.FirstName, document.UploadedByUser.LastName),
                document.IsVerified,
                document.FileSizeBytes,
                document.Description,
                "CustomerKyc")))
            .OrderByDescending(document => document.UploadedAt)
            .ToList();

        return HandleSuccessResponse(documents);
    }

    /// <summary>
    /// Get service requests pending review
    /// </summary>
    [HttpGet("service-reviews")]
    [HttpGet("GetPendingServiceReviews")]
    public async Task<IActionResult> GetPendingServiceReviews(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var pending = await _context.Set<Shipment>()
            .AsNoTracking()
            .Where(s =>
                (s.Status == ShipmentStatus.Submitted || s.Status == ShipmentStatus.UnderReview) &&
                s.RecordStatus == RecordStatus.Active &&
                (!s.AssignedAssessorId.HasValue || s.AssignedAssessorId == currentUserId.Value))
            .OrderByDescending(s => s.CreateAt)
            .Select(s => new
            {
                s.Id,
                s.TrackingNumber,
                s.ServiceCode,
                s.ReferenceNumber,
                s.Title,
                s.ImporterId,
                Importer = new
                {
                    s.Importer.Id,
                    s.Importer.FirstName,
                    s.Importer.LastName,
                    s.Importer.Username,
                    s.Importer.Email,
                    s.Importer.Phone
                },
                Customer = _context.Set<Customer>()
                    .Where(customer => customer.UserId == s.ImporterId &&
                                       customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new
                    {
                        customer.Id,
                        customer.BusinessName,
                        customer.TINNumber,
                        customer.BusinessLicense,
                        customer.ImportLicense,
                        customer.ImportLicenseExpiry,
                        customer.BusinessAddress,
                        customer.City,
                        customer.State,
                        customer.PostalCode,
                        customer.ContactPerson,
                        customer.ContactPhone,
                        customer.ContactEmail,
                        customer.BusinessType,
                        customer.IsVerified,
                        Documents = customer.Documents
                            .Where(document => document.RecordStatus == RecordStatus.Active)
                            .Select(document => new
                            {
                                document.Id,
                                document.OriginalFileName,
                                document.DocumentType,
                                document.Description,
                                document.IsRequired,
                                document.IsVerified,
                                document.ExpiryDate,
                                document.VerificationNotes,
                                document.CreateAt,
                                document.UpdatedAt
                            })
                            .ToList()
                    })
                    .FirstOrDefault(),
                s.Description,
                s.Mode,
                s.AssignedHub,
                s.Status,
                s.RouteCategory,
                s.DeclaredValue,
                s.TaxCategory,
                s.CountryOfOrigin,
                s.RiskLevel,
                s.ManagerRiskChannel,
                s.Origin,
                s.Destination,
                s.AssignedCaseExecutorId,
                AssignedCaseExecutor = s.AssignedCaseExecutor == null
                    ? null
                    : new
                    {
                        s.AssignedCaseExecutor.Id,
                        s.AssignedCaseExecutor.FirstName,
                        s.AssignedCaseExecutor.LastName,
                        s.AssignedCaseExecutor.Username
                    },
                s.ServiceType,
                s.AssignedAssessorId,
                s.AssignmentNotes,
                s.CreateAt,
                s.UpdatedAt,
                Documents = s.Documents
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .Select(document => new
                    {
                        document.Id,
                        document.OriginalFileName,
                        document.DocumentType,
                        document.Description,
                        document.FileSizeBytes,
                        document.MimeType,
                        document.IsRequired,
                        document.IsVerified,
                        document.VerificationNotes,
                        document.CreateAt,
                        document.UpdatedAt
                    })
                    .ToList(),
                Stages = s.Stages
                    .Where(stage => stage.RecordStatus == RecordStatus.Active)
                    .Select(stage => new
                    {
                        stage.Id,
                        workflowStageId = stage.Id,
                        stage.StageNumber,
                        stage.Stage,
                        stage.Status,
                        stage.RiskChannel,
                        stage.StageNote,
                        stage.CreateAt,
                        stage.UpdatedAt,
                        Documents = stage.Documents
                            .Where(document => document.RecordStatus == RecordStatus.Active)
                            .Select(document => new
                            {
                                document.Id,
                                document.OriginalFileName,
                                document.DocumentType,
                                document.Description,
                                document.IsVerified,
                                document.CreateAt
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(pending);
    }

    /// <summary>
    /// Review and approve/reject a service request
    /// </summary>
    [HttpPut("ReviewService")]
    public async Task<IActionResult> ReviewService(
        [FromBody] ShipmentReviewRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.ShipmentId <= 0)
            return BadRequest("A valid shipment ID is required.");

        var service = await _context.Set<Shipment>()
            .FirstOrDefaultAsync(s =>
                s.Id == request.ShipmentId &&
                s.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (service == null)
            return NotFound("Service not found");
        if (service.Status != ShipmentStatus.Submitted)
            return Conflict("Only submitted services can be reviewed.");
        if (!request.IsApproved && string.IsNullOrWhiteSpace(request.ReviewNotes))
            return BadRequest("Review notes are required when rejecting a service.");

        if (request.IsApproved)
        {
            service.UpdateStatus(ShipmentStatus.Approved);
            service.AssignAssessor(currentUserId.Value);
        }
        else
        {
            service.UpdateStatus(ShipmentStatus.Rejected);
        }

        if (!string.IsNullOrWhiteSpace(request.ReviewNotes))
        {
            var reviewComment = ServiceMessage.Create(
                "Service Review",
                request.ReviewNotes.Trim(),
                MessageType.System,
                service.Id,
                currentUserId.Value,
                service.CreatedByUserId
            );

            _context.Set<ServiceMessage>().Add(reviewComment);
        }

        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            request.IsApproved ? "Approved" : "Rejected",
            "Service",
            service.ServiceCode.Length == 0 ? service.TrackingNumber : service.ServiceCode,
            request.ReviewNotes?.Trim() ?? $"Service review decision: {(request.IsApproved ? "Approved" : "Rejected")}.",
            serviceId: service.Id));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(service);
    }

    [HttpPut("services/risk-channel")]
    [HttpPut("UpdateRiskChannel")]
    public async Task<IActionResult> UpdateRiskChannel(
        [FromBody] UpdateAssessorRiskChannelRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.ShipmentId <= 0 ||
            !Enum.TryParse<RiskLevel>(request.RiskChannel?.Trim(), true, out var riskLevel) ||
            !Enum.IsDefined(riskLevel))
            return BadRequest("A valid service ID and risk channel (Green, Yellow, Blue, or Red) are required.");

        var shipment = await _context.Shipments
            .FirstOrDefaultAsync(service =>
                service.Id == request.ShipmentId &&
                service.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (shipment == null)
            return NotFound("Service not found.");

        if (request.StageId.HasValue)
        {
            var stage = await _context.ServiceStageExecutions.FirstOrDefaultAsync(item =>
                item.Id == request.StageId.Value &&
                item.ShipmentId == shipment.Id &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
            if (stage == null)
                return NotFound("Service stage not found.");
            stage.UpdateAssessorRiskChannel(riskLevel.ToString(), currentUserId.Value);
            shipment.UpdateAssessorRiskChannel(riskLevel);
        }
        else
        {
            shipment.UpdateAssessorRiskChannel(riskLevel);
        }

        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            "Risk channel assigned",
            request.StageId.HasValue ? "ServiceStage" : "Service",
            shipment.ServiceCode.Length == 0 ? shipment.TrackingNumber : shipment.ServiceCode,
            $"Risk channel set to {riskLevel}.{(string.IsNullOrWhiteSpace(request.Notes) ? string.Empty : $" {request.Notes.Trim()}")}",
            serviceId: shipment.Id,
            stageId: request.StageId));
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            shipment.Id,
            request.StageId,
            "Risk channel updated",
            $"Risk channel set to {riskLevel}.{(string.IsNullOrWhiteSpace(request.Notes) ? string.Empty : $" {request.Notes.Trim()}")}",
            currentUserId.Value,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            ShipmentId = shipment.Id,
            RiskChannel = shipment.ManagerRiskChannel,
            shipment.RiskLevel
        });
    }

    /// <summary>
    /// Get services under assessor oversight
    /// </summary>
    [HttpGet("GetServicesUnderOversight")]
    public async Task<IActionResult> GetServicesUnderOversight(
        [FromQuery] ShipmentStatus? status = null,
        [FromQuery] ServiceType? type = null)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();

        var query = _context.Set<Shipment>()
            .Include(s => s.Importer)
            .Include(s => s.AssignedCaseExecutor)
            .Include(s => s.Stages)
            .Where(s =>
                s.AssignedAssessorId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active);

        if (status.HasValue)
            query = query.Where(s => s.Status == status.Value);

        if (type.HasValue)
            query = query.Where(s => s.ServiceType == type.Value);

        var services = await query
            .OrderByDescending(s => s.CreateAt)
            .ToListAsync();

        return HandleSuccessResponse(services);
    }

    /// <summary>
    /// Get all active service records for the assessor service register.
    /// </summary>
    [HttpGet("services")]
    [HttpGet("GetActiveServicesForRegister")]
    public async Task<IActionResult> GetActiveServicesForRegister(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var services = await _context.Set<Shipment>()
            .AsNoTracking()
            .Where(shipment => shipment.RecordStatus == RecordStatus.Active)
            .OrderByDescending(shipment => shipment.CreateAt)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.TrackingNumber,
                shipment.ServiceCode,
                shipment.ReferenceNumber,
                shipment.Title,
                shipment.ImporterId,
                Importer = new
                {
                    shipment.Importer.Id,
                    shipment.Importer.FirstName,
                    shipment.Importer.LastName,
                    shipment.Importer.Username
                },
                Customer = _context.Set<Customer>()
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new
                    {
                        customer.Id,
                        customer.BusinessName,
                        customer.TINNumber
                    })
                    .FirstOrDefault(),
                shipment.Description,
                shipment.Mode,
                shipment.Status,
                shipment.RiskLevel,
                shipment.ManagerRiskChannel,
                shipment.AssignedCaseExecutorId,
                AssignedCaseExecutor = shipment.AssignedCaseExecutor == null
                    ? null
                    : new
                    {
                        shipment.AssignedCaseExecutor.Id,
                        shipment.AssignedCaseExecutor.FirstName,
                        shipment.AssignedCaseExecutor.LastName,
                        shipment.AssignedCaseExecutor.Username
                    },
                shipment.ServiceType,
                shipment.AssignedAssessorId,
                shipment.AssignmentNotes,
                shipment.Origin,
                shipment.Destination,
                shipment.CreateAt,
                shipment.UpdatedAt,
                shipment.RecordStatus
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(services);
    }

    /// <summary>
    /// Add compliance feedback to a service
    /// </summary>
    [HttpPost("AddComplianceFeedback")]
    public async Task<IActionResult> AddComplianceFeedback([FromBody] ComplianceFeedbackRequest request)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();
        if (request.ShipmentId <= 0 || string.IsNullOrWhiteSpace(request.Feedback))
            return BadRequest("A valid shipment ID and feedback are required.");

        var service = await _context.Set<Shipment>()
            .FirstOrDefaultAsync(s =>
                s.Id == request.ShipmentId &&
                s.RecordStatus == RecordStatus.Active);

        if (service == null)
            return NotFound("Service not found");

        var feedback = ServiceMessage.Create(
            "Compliance Feedback",
            request.Feedback,
            MessageType.System,
            request.ShipmentId,
            currentUserId.Value,
            service.AssignedCaseExecutorId,
            isUrgent: true
        );

        _context.Set<ServiceMessage>().Add(feedback);
        await _context.SaveChangesAsync();

        return HandleSuccessResponse(feedback);
    }

    /// <summary>
    /// Get assessor dashboard
    /// </summary>
    [HttpGet("Dashboard")]
    [HttpGet("GetDashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var pendingCustomerApprovalsCount = await _context.Set<Customer>()
            .CountAsync(c =>
                !c.IsVerified &&
                !c.IsReturnedForRevision &&
                c.RecordStatus == RecordStatus.Active &&
                c.VerificationStatus != "Rejected",
                cancellationToken);

        var returnedCustomersForRevisionCount = await _context.Set<Customer>()
            .CountAsync(c =>
                !c.IsVerified &&
                c.IsReturnedForRevision &&
                c.RecordStatus == RecordStatus.Active,
                cancellationToken);

        var verifiedCustomersCount = await _context.Set<Customer>()
            .CountAsync(c =>             c.IsVerified && c.RecordStatus == RecordStatus.Active,
            cancellationToken);

        var pendingCustomerDocumentsCount = await _context.Set<CustomerDocument>()
            .CountAsync(document =>
                !document.IsVerified &&
                document.RecordStatus == RecordStatus.Active &&
                document.Customer != null &&
                !document.Customer.IsVerified &&
                document.Customer.RecordStatus == RecordStatus.Active,
                cancellationToken);

        var decisionWindowStart = DateTime.UtcNow.AddDays(-30);
        var recentCustomerDecisionDurations = await _context.Set<Customer>()
            .AsNoTracking()
            .Where(customer =>
                customer.VerifiedByUserId == currentUserId.Value &&
                customer.VerifiedAt.HasValue &&
                customer.VerifiedAt.Value >= decisionWindowStart)
            .Select(customer => new
            {
                customer.CreateAt,
                VerifiedAt = customer.VerifiedAt!.Value
            })
            .ToListAsync(cancellationToken);
        var orderedDecisionDurations = recentCustomerDecisionDurations
            .Select(decision => Math.Max(0, (decision.VerifiedAt - decision.CreateAt).TotalDays))
            .OrderBy(days => days)
            .ToArray();
        decimal? medianCustomerDecisionDays = orderedDecisionDurations.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => (decimal)Math.Round(orderedDecisionDurations[count / 2], 1),
            var count => (decimal)Math.Round(
                (orderedDecisionDurations[count / 2 - 1] + orderedDecisionDurations[count / 2]) / 2,
                1)
        };

        var pendingServiceIds = await _context.Set<Shipment>()
            .Where(s =>
                (s.Status == ShipmentStatus.Submitted || s.Status == ShipmentStatus.UnderReview) &&
                s.RecordStatus == RecordStatus.Active &&
                (!s.AssignedAssessorId.HasValue || s.AssignedAssessorId == currentUserId.Value))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var openCasesCount = await _context.Set<Shipment>()
            .CountAsync(s =>
                s.AssignedAssessorId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active &&
                (s.Status == ShipmentStatus.UnderReview ||
                 s.Status == ShipmentStatus.Approved ||
                 s.Status == ShipmentStatus.InProgress),
                cancellationToken);

        var completedReviewsTodayCount = await _context.Set<Shipment>()
            .CountAsync(s => s.AssignedAssessorId == currentUserId.Value &&
                             s.RecordStatus == RecordStatus.Active &&
                             s.UpdatedAt.HasValue && s.UpdatedAt.Value.Date == DateTime.UtcNow.Date,
                cancellationToken);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);
        var approvedCustomersThisMonth = await _context.Set<Customer>()
            .CountAsync(c =>
                c.VerifiedByUserId == currentUserId.Value &&
                c.VerifiedAt.HasValue &&
                c.VerifiedAt.Value >= monthStart &&
                c.VerifiedAt.Value < nextMonthStart,
                cancellationToken);
        var approvedServicesThisMonth = await _context.Set<Shipment>()
            .CountAsync(s =>
                s.AssignedAssessorId == currentUserId.Value &&
                s.Status == ShipmentStatus.Approved &&
                s.UpdatedAt.HasValue &&
                s.UpdatedAt.Value >= monthStart &&
                s.UpdatedAt.Value < nextMonthStart,
                cancellationToken);

        var complianceServiceIds = await _context.Set<ServiceMessage>()
            .Where(m => m.MessageType == MessageType.System &&
                        m.Subject.Contains("Compliance") &&
                        m.IsUrgent &&
                                m.ServiceId.HasValue &&
                                m.Service != null &&
                                m.Service.RecordStatus == RecordStatus.Active)
            .Select(m => m.ServiceId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var riskChannels = new
        {
            Green = await _context.Shipments.CountAsync(service =>
                service.RecordStatus == RecordStatus.Active &&
                (service.ManagerRiskChannel.ToLower() == "green" || service.RiskLevel == RiskLevel.Green),
                cancellationToken),
            Yellow = await _context.Shipments.CountAsync(service =>
                service.RecordStatus == RecordStatus.Active &&
                (service.ManagerRiskChannel.ToLower() == "yellow" || service.RiskLevel == RiskLevel.Yellow),
                cancellationToken),
            Blue = await _context.Shipments.CountAsync(service =>
                service.RecordStatus == RecordStatus.Active &&
                (service.ManagerRiskChannel.ToLower() == "blue" || service.RiskLevel == RiskLevel.Blue),
                cancellationToken),
            Red = await _context.Shipments.CountAsync(service =>
                service.RecordStatus == RecordStatus.Active &&
                (service.ManagerRiskChannel.ToLower() == "red" || service.RiskLevel == RiskLevel.Red),
                cancellationToken)
        };
        var dueWithin48Hours = await _context.Shipments.CountAsync(service =>
            service.RecordStatus == RecordStatus.Active &&
            service.ETA.HasValue &&
            service.ETA.Value >= DateTime.UtcNow &&
            service.ETA.Value <= DateTime.UtcNow.AddHours(48) &&
            service.Status != ShipmentStatus.Completed &&
            service.Status != ShipmentStatus.Rejected &&
            service.Status != ShipmentStatus.Cancelled,
            cancellationToken);
        var riskServiceIds = await _context.Set<Shipment>()
            .Where(s =>
                s.AssignedAssessorId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active &&
                (s.Status == ShipmentStatus.UnderReview ||
                 s.Status == ShipmentStatus.Approved ||
                 s.Status == ShipmentStatus.InProgress) &&
                (s.RiskLevel == RiskLevel.Red || s.RiskLevel == RiskLevel.Yellow))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        var flaggedStageServiceIds = await _context.Set<ServiceStageExecution>()
            .Where(stage =>
                stage.ShipmentId.HasValue &&
                stage.Shipment != null &&
                stage.Shipment.AssignedAssessorId == currentUserId.Value &&
                stage.Shipment.RecordStatus == RecordStatus.Active &&
                stage.RecordStatus == RecordStatus.Active &&
                (stage.Shipment.Status == ShipmentStatus.UnderReview ||
                 stage.Shipment.Status == ShipmentStatus.Approved ||
                 stage.Shipment.Status == ShipmentStatus.InProgress) &&
                (stage.IsBlocked ||
                 stage.RequiresCustomerAction ||
                 stage.Status == StageStatus.NeedsReview))
            .Select(stage => stage.ShipmentId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var servicesNeedingAttention = pendingServiceIds
            .Concat(complianceServiceIds)
            .Concat(riskServiceIds)
            .Concat(flaggedStageServiceIds)
            .ToHashSet();

        var recentCustomerApprovals = await _context.Set<Customer>()
            .Include(c => c.User)
            .Where(c => c.VerifiedByUserId == currentUserId.Value)
            .OrderByDescending(c => c.VerifiedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        var recentServiceReviews = await _context.Set<Shipment>()
            .Include(s => s.Importer)
            .Where(s => s.AssignedAssessorId == currentUserId.Value)
            .OrderByDescending(s => s.UpdatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        var dashboard = new
        {
            PendingCustomerApprovals = pendingCustomerApprovalsCount,
            ReturnedCustomersForRevision = returnedCustomersForRevisionCount,
            VerifiedCustomers = verifiedCustomersCount,
            PendingCustomerDocuments = pendingCustomerDocumentsCount,
            MedianCustomerDecisionDays = medianCustomerDecisionDays,
            PendingServiceReviews = pendingServiceIds.Count,
            ServicesUnderOversight = openCasesCount,
            OpenCases = openCasesCount,
            NeedsAttention = pendingCustomerApprovalsCount + servicesNeedingAttention.Count,
            DueWithin48Hours = dueWithin48Hours,
            RiskChannels = riskChannels,
            ClearedThisMonth = approvedCustomersThisMonth + approvedServicesThisMonth,
            CompletedReviewsToday = completedReviewsTodayCount,
            RecentCustomerApprovals = recentCustomerApprovals,
            RecentServiceReviews = recentServiceReviews
        };

        return HandleSuccessResponse(dashboard);
    }

    [HttpGet("AssessmentLogs")]
    [HttpGet("GetAssessmentLogs")]
    public async Task<IActionResult> GetAssessmentLogs(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var customerApprovals = await _context.Set<Customer>()
            .AsNoTracking()
            .Where(customer =>
                customer.VerifiedByUserId == currentUserId.Value &&
                customer.VerifiedAt.HasValue)
            .Select(customer => new AssessmentActivityRecord(
                $"customer-{customer.Id}",
                "Customer approved",
                customer.BusinessName,
                $"Customer {customer.BusinessName} was verified.",
                customer.VerifiedAt!.Value,
                "Customer approvals"))
            .ToListAsync(cancellationToken);

        var serviceApprovals = await _context.Set<Shipment>()
            .AsNoTracking()
            .Where(shipment =>
                shipment.AssignedAssessorId == currentUserId.Value &&
                shipment.Status == ShipmentStatus.Approved &&
                shipment.UpdatedAt.HasValue)
            .Select(shipment => new AssessmentActivityRecord(
                $"service-{shipment.Id}",
                "Service approved",
                shipment.TrackingNumber,
                $"Service {shipment.TrackingNumber} was approved.",
                shipment.UpdatedAt!.Value,
                "Service reviews"))
            .ToListAsync(cancellationToken);

        var serviceReviewFeedback = await _context.Set<ServiceMessage>()
            .AsNoTracking()
            .Where(message =>
                message.SenderUserId == currentUserId.Value &&
                message.Subject == "Service Review" &&
                message.Service != null &&
                message.Service.RecordStatus == RecordStatus.Active)
            .Select(message => new AssessmentActivityRecord(
                $"service-review-feedback-{message.Id}",
                "Service review feedback recorded",
                message.Service!.TrackingNumber,
                message.Content,
                message.CreatedDate,
                "Service reviews"))
            .ToListAsync(cancellationToken);

        var serviceActivityRecords = await _context.ServiceActivityLogs
            .AsNoTracking()
            .Where(activity =>
                activity.ActorUserId == currentUserId.Value &&
                activity.RecordStatus == RecordStatus.Active)
            .Join(
                _context.Shipments.AsNoTracking().Where(shipment =>
                    shipment.RecordStatus == RecordStatus.Active),
                activity => activity.ServiceId,
                shipment => shipment.Id,
                (activity, shipment) => new { activity, shipment })
            .OrderByDescending(item => item.activity.CreatedAt)
            .Take(100)
            .Select(item => new AssessmentActivityRecord(
                $"service-activity-{item.activity.Id}",
                item.activity.ActionTitle,
                item.shipment.TrackingNumber,
                item.activity.Description,
                item.activity.CreatedAt,
                item.activity.StageId.HasValue ? "Service stages" : "Risk & compliance"))
            .ToListAsync(cancellationToken);

        var persistedLogs = await _context.AssessmentLogs
            .AsNoTracking()
            .Where(log =>
                log.ActorUserId == currentUserId.Value &&
                log.RecordStatus == RecordStatus.Active)
            .OrderByDescending(log => log.OccurredAt)
            .Take(100)
            .Select(log => new AssessmentActivityRecord(
                $"assessment-{log.Id}",
                log.Action,
                log.Reference,
                log.Description,
                log.OccurredAt,
                log.TargetType))
            .ToListAsync(cancellationToken);

        var activities = customerApprovals
            .Concat(serviceApprovals)
            .Concat(serviceReviewFeedback)
            .Concat(serviceActivityRecords)
            .Concat(persistedLogs)
            .OrderByDescending(activity => activity.Timestamp)
            .Take(100)
            .ToList();

        return HandleSuccessResponse(activities);
    }

    /// <summary>
    /// Get compliance issues flagged
    /// </summary>
    [HttpGet("GetComplianceIssues")]
    public async Task<IActionResult> GetComplianceIssues(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();

        var issues = await _context.Set<ServiceMessage>()
            .Include(m => m.Service)
            .Include(m => m.SenderUser)
            .Where(m => m.MessageType == MessageType.System &&
                        m.Subject.Contains("Compliance") &&
                        m.IsUrgent)
            .OrderByDescending(m => m.Id)
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(issues);
    }

    [HttpGet("services/{id:long}")]
    [HttpGet("service-reviews/{id:long}")]
    [HttpGet("GetServiceDetails/{id:long}")]
    public async Task<IActionResult> GetServiceDetails(long id, CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();

        var service = await _context.Shipments
            .AsNoTracking()
            .Where(shipment => shipment.Id == id && shipment.RecordStatus == RecordStatus.Active)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.TrackingNumber,
                shipment.ServiceCode,
                shipment.ReferenceNumber,
                shipment.Title,
                shipment.WorkflowStatus,
                shipment.ManagerRiskChannel,
                CurrentRiskChannel = string.IsNullOrWhiteSpace(shipment.ManagerRiskChannel)
                    ? shipment.RiskLevel.HasValue ? shipment.RiskLevel.Value.ToString() : "Unassigned"
                    : shipment.ManagerRiskChannel,
                shipment.Description,
                shipment.Mode,
                shipment.ServiceType,
                shipment.Status,
                shipment.RouteCategory,
                shipment.DeclaredValue,
                shipment.TaxCategory,
                shipment.CountryOfOrigin,
                shipment.RiskLevel,
                shipment.Origin,
                shipment.Destination,
                shipment.AssignedHub,
                shipment.AssignmentNotes,
                shipment.CreateAt,
                shipment.UpdatedAt,
                Importer = new
                {
                    shipment.Importer.Id,
                    shipment.Importer.FirstName,
                    shipment.Importer.LastName,
                    shipment.Importer.Username,
                    shipment.Importer.Email,
                    shipment.Importer.Phone
                },
                Customer = _context.Customers
                    .Where(customer =>
                        customer.UserId == shipment.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new
                    {
                        customer.Id,
                        customer.BusinessName,
                        customer.TINNumber,
                        customer.BusinessAddress,
                        customer.ContactPerson,
                        customer.ContactPhone,
                        customer.ContactEmail
                    })
                    .FirstOrDefault(),
                AssignedCaseExecutor = shipment.AssignedCaseExecutor == null
                    ? null
                    : new
                    {
                        shipment.AssignedCaseExecutor.Id,
                        shipment.AssignedCaseExecutor.FirstName,
                        shipment.AssignedCaseExecutor.LastName,
                        shipment.AssignedCaseExecutor.Username
                    },
                AssignedAssessor = shipment.AssignedAssessor == null
                    ? null
                    : new
                    {
                        shipment.AssignedAssessor.Id,
                        shipment.AssignedAssessor.FirstName,
                        shipment.AssignedAssessor.LastName,
                        shipment.AssignedAssessor.Username
                    },
                Stages = shipment.Stages
                    .Where(stage => stage.RecordStatus == RecordStatus.Active)
                    .OrderBy(stage => stage.Stage)
                    .Select(stage => new
                    {
                        stage.Id,
                        workflowStageId = stage.Id,
                        stage.StageNumber,
                        stage.Stage,
                        stage.Status,
                        stage.RiskChannel,
                        stage.StageNote,
                        stage.SummaryNote,
                        stage.RequiresCustomerAction,
                        stage.IsBlocked,
                        stage.BlockReason,
                        stage.StartedAt,
                        stage.CompletedAt,
                        stage.CompletedByName,
                        stage.UpdatedByUserId,
                        Documents = stage.Documents
                            .Where(document => document.RecordStatus == RecordStatus.Active)
                            .Select(document => new
                            {
                                document.Id,
                                document.OriginalFileName,
                                document.DocumentType,
                                document.Description,
                                document.IsVerified,
                                document.CreateAt
                            })
                            .ToList()
                    })
                    .ToList(),
                Documents = shipment.Documents
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(document => document.CreateAt)
                    .Select(document => new
                    {
                        document.Id,
                        document.OriginalFileName,
                        document.DocumentType,
                        document.Description,
                        document.IsRequired,
                        document.IsVerified,
                        document.CreateAt,
                        document.ShipmentStageId,
                        UploadedBy = document.UploadedByUser == null
                            ? null
                            : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
                    })
                    .ToList(),
                Assessments = _context.ServiceAssessments
                    .Where(assessment =>
                        assessment.ServiceId == shipment.Id &&
                        assessment.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(assessment => assessment.CreateAt)
                    .Select(assessment => new
                    {
                        assessment.Id,
                        assessment.Decision,
                        assessment.TaxAmount,
                        assessment.DutyAmount,
                        assessment.Notes,
                        assessment.CreateAt,
                        Assessor = assessment.Assessor == null
                            ? null
                            : assessment.Assessor.FirstName + " " + assessment.Assessor.LastName
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return service == null
            ? NotFound("Service not found.")
            : HandleSuccessResponse(service);
    }

    [HttpGet("services/{serviceId:long}/stages")]
    [HttpGet("GetServiceStages/{serviceId:long}")]
    public async Task<IActionResult> GetServiceStages(long serviceId, CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (!await _context.Shipments.AnyAsync(shipment =>
                shipment.Id == serviceId &&
                shipment.RecordStatus == RecordStatus.Active,
                cancellationToken))
            return NotFound("Service not found.");

        var stages = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Where(stage =>
                stage.ShipmentId == serviceId &&
                stage.RecordStatus == RecordStatus.Active)
            .OrderBy(stage => stage.Stage)
            .Select(stage => new
            {
                stage.Id,
                workflowStageId = stage.Id,
                stage.StageNumber,
                stage.Stage,
                stage.Status,
                stage.RiskChannel,
                stage.StageNote,
                stage.SummaryNote,
                stage.Comments,
                stage.RequiresCustomerAction,
                stage.IsBlocked,
                stage.BlockReason,
                stage.StartedAt,
                stage.CompletedAt,
                stage.CompletedByName,
                stage.UpdatedAt,
                Documents = stage.Documents
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .Select(document => new
                    {
                        document.Id,
                        document.OriginalFileName,
                        document.DocumentType,
                        document.Description,
                        document.IsVerified,
                        document.CreateAt
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(stages);
    }

    [HttpGet("DownloadStageDocument")]
    public async Task<IActionResult> DownloadStageDocument(
        [FromQuery] long serviceId,
        [FromQuery] long stageId,
        [FromQuery] long documentId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();

        var belongsToService = await _context.StageDocuments.AnyAsync(document =>
            document.Id == documentId &&
            document.RecordStatus == RecordStatus.Active &&
            document.ServiceStageId == stageId &&
            _context.ServiceStageExecutions.Any(stage =>
                stage.Id == stageId &&
                stage.ShipmentId == serviceId &&
                stage.RecordStatus == RecordStatus.Active &&
                stage.Shipment != null &&
                stage.Shipment.RecordStatus == RecordStatus.Active),
            cancellationToken);
        if (!belongsToService)
            return NotFound("Service stage document not found.");

        var result = await _mediator.Send(new DownloadStageDocumentQuery
        {
            DocumentId = documentId
        }, cancellationToken);
        if (result.IsError || result.Payload == null)
            return HandleErrorResponse(result.Errors);

        var physicalPath = ResolveDocumentPath(result.Payload.FilePath);
        if (physicalPath == null)
            return NotFound("Document file was not found on the server.");

        var bytes = await System.IO.File.ReadAllBytesAsync(physicalPath, cancellationToken);
        return File(
            bytes,
            string.IsNullOrWhiteSpace(result.Payload.MimeType)
                ? "application/octet-stream"
                : result.Payload.MimeType,
            System.IO.Path.GetFileName(result.Payload.OriginalFileName));
    }

    [HttpPost("DownloadStageDocuments")]
    public async Task<IActionResult> DownloadStageDocuments(
        [FromBody] DownloadAssessorStageDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();
        if (request.ServiceId <= 0 || request.StageId <= 0)
            return BadRequest("A valid service and stage ID are required.");
        if (request.DocumentIds.Count == 0)
            return BadRequest("Select at least one document to download.");

        var distinctIds = request.DocumentIds.Distinct().ToList();
        var documentsInServiceCount = await _context.StageDocuments.CountAsync(document =>
            distinctIds.Contains(document.Id) &&
            document.RecordStatus == RecordStatus.Active &&
            document.ServiceStageId == request.StageId &&
            _context.ServiceStageExecutions.Any(stage =>
                stage.Id == request.StageId &&
                stage.ShipmentId == request.ServiceId &&
                stage.RecordStatus == RecordStatus.Active &&
                stage.Shipment != null &&
                stage.Shipment.RecordStatus == RecordStatus.Active),
            cancellationToken);
        if (documentsInServiceCount != distinctIds.Count)
            return NotFound("One or more service stage documents were not found.");

        var result = await _mediator.Send(new DownloadMultipleStageDocumentsQuery
        {
            DocumentIds = distinctIds
        }, cancellationToken);
        return result.IsError || result.Payload == null
            ? HandleErrorResponse(result.Errors)
            : File(result.Payload, "application/zip", "assessor-stage-documents.zip");
    }

    private string? ResolveDocumentPath(string filePath)
    {
        if (System.IO.Path.IsPathRooted(filePath))
            return System.IO.File.Exists(filePath) ? filePath : null;

        var candidates = new[]
        {
            System.IO.Path.Combine(_environment.WebRootPath ?? string.Empty, filePath),
            System.IO.Path.Combine(_environment.ContentRootPath, filePath),
            System.IO.Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", filePath),
            System.IO.Path.Combine(Directory.GetCurrentDirectory(), filePath)
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    [HttpPost("DecideServiceStage")]
    public async Task<IActionResult> DecideServiceStage(
        [FromBody] DecideServiceStageRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.ServiceId <= 0 || request.StageId <= 0)
            return BadRequest("A valid service and stage ID are required.");

        var action = request.Action?.Trim();
        if (action is not ("Approve" or "ReturnToExecutor" or "Reject"))
            return BadRequest("Action must be Approve, ReturnToExecutor, or Reject.");
        if (action != "Approve" && string.IsNullOrWhiteSpace(request.Notes))
            return BadRequest("A reason is required when returning or rejecting a stage.");

        var stage = await _context.ServiceStageExecutions
            .Include(item => item.Shipment)
            .FirstOrDefaultAsync(item =>
                item.Id == request.StageId &&
                item.ShipmentId == request.ServiceId &&
                item.RecordStatus == RecordStatus.Active &&
                item.Shipment != null &&
                item.Shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (stage == null)
            return NotFound("Service stage not found.");
        if (stage.Status is StageStatus.NotStarted or StageStatus.Completed)
            return Conflict("Only started, incomplete stages can receive an assessor decision.");

        var status = action switch
        {
            "Approve" => StageStatus.Completed,
            "ReturnToExecutor" => StageStatus.NeedsReview,
            "Reject" => StageStatus.Blocked,
            _ => throw new InvalidOperationException("Unsupported service stage decision.")
        };
        var notes = request.Notes?.Trim();
        stage.UpdateStatus(status, notes, currentUserId.Value);
        stage.SetBlocked(action == "Reject", action == "Reject" ? notes : null, currentUserId.Value);
        stage.SetCustomerActionRequired(false);

        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            action,
            "ServiceStage",
            stage.Shipment?.ServiceCode ?? stage.Shipment?.TrackingNumber ?? request.ServiceId.ToString(),
            string.IsNullOrWhiteSpace(notes) ? $"Stage {action.ToLowerInvariant()}." : notes,
            serviceId: request.ServiceId,
            stageId: request.StageId));
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            request.ServiceId,
            request.StageId,
            $"Stage {action}",
            string.IsNullOrWhiteSpace(notes) ? $"Stage {action.ToLowerInvariant()}." : notes,
            currentUserId.Value,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            stage.Id,
            ServiceId = request.ServiceId,
            stage.Status,
            stage.Comments,
            stage.IsBlocked,
            stage.RequiresCustomerAction,
            stage.UpdatedAt
        });
    }

    [HttpPost("RecordAssessment")]
    [HttpPost("services/assessment")]
    public async Task<IActionResult> RecordAssessment(
        [FromBody] RecordAssessmentRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.ServiceId <= 0 ||
            string.IsNullOrWhiteSpace(request.Decision) ||
            request.TaxAmount < 0 ||
            request.DutyAmount < 0)
            return BadRequest("A service, decision, and non-negative tax and duty amounts are required.");
        var normalizedDecision = NormalizeDecision(request.Decision);
        var decision = normalizedDecision switch
        {
            "Approve" or "Approved" or "SignOff" => "Approved",
            "NeedsReview" => "NeedsReview",
            "Reject" or "Rejected" => "Rejected",
            "RequestChanges" => "RequestChanges",
            "RequiresCustomerAction" => "RequiresCustomerAction",
            _ => null
        };
        if (decision == null)
            return BadRequest("Decision must be Approve, Sign-off, NeedsReview, Reject, or RequestChanges.");
        var assessorNote = (request.AssessorNote ?? request.Notes)?.Trim();
        if (decision is "NeedsReview" or "Rejected" or "RequestChanges" or "RequiresCustomerAction" &&
            string.IsNullOrWhiteSpace(assessorNote))
            return BadRequest("assessorNote is required for this assessment decision.");

        var service = await _context.Shipments
            .FirstOrDefaultAsync(shipment =>
                shipment.Id == request.ServiceId &&
                shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (service == null)
            return NotFound("Service not found.");

        ServiceStageExecution? assessmentStage;
        if (request.StageId.HasValue)
        {
            assessmentStage = await _context.ServiceStageExecutions
                .FirstOrDefaultAsync(stage =>
                    stage.Id == request.StageId.Value &&
                    stage.ShipmentId == service.Id &&
                    stage.RecordStatus == RecordStatus.Active,
                    cancellationToken);
            if (assessmentStage == null)
                return NotFound("Service stage not found.");
        }
        else
        {
            assessmentStage = await _context.ServiceStageExecutions
                .FirstOrDefaultAsync(stage =>
                    stage.ShipmentId == service.Id &&
                    stage.Stage == ShipmentStage.AssessmentandTaxPayment &&
                    stage.RecordStatus == RecordStatus.Active,
                    cancellationToken);
        }

        var assessment = ServiceAssessment.Create(
            service.Id,
            currentUserId.Value,
            decision,
            request.TaxAmount,
            request.DutyAmount,
            assessorNote);
        _context.ServiceAssessments.Add(assessment);

        var serviceStatus = decision switch
        {
            "Approved" => ShipmentStatus.Approved,
            "Rejected" => ShipmentStatus.Rejected,
            _ => ShipmentStatus.UnderReview
        };
        service.UpdateStatus(serviceStatus);
        service.AssignAssessor(currentUserId.Value);

        if (assessmentStage != null)
        {
            var stageStatus = decision switch
            {
                "Approved" => StageStatus.Completed,
                "NeedsReview" => StageStatus.NeedsReview,
                "Rejected" => StageStatus.Blocked,
                "RequestChanges" or "RequiresCustomerAction" => StageStatus.RequiresCustomerAction,
                _ => StageStatus.NeedsReview
            };
            assessmentStage.UpdateStatus(stageStatus, assessorNote, currentUserId.Value);
            assessmentStage.SetBlocked(decision == "Rejected", decision == "Rejected" ? assessorNote : null, currentUserId.Value);
            assessmentStage.SetCustomerActionRequired(
                decision is "RequestChanges" or "RequiresCustomerAction");
        }

        var actorName = await GetAssessorNameAsync(currentUserId.Value, cancellationToken);
        var serviceReference = string.IsNullOrWhiteSpace(service.ServiceCode)
            ? service.TrackingNumber
            : service.ServiceCode;
        _context.AssessmentLogs.Add(AssessmentLog.Create(
            currentUserId.Value,
            actorName,
            decision,
            assessmentStage == null ? "Service" : "ServiceStage",
            serviceReference,
            assessorNote ?? $"Service assessment decision: {decision}.",
            serviceId: service.Id,
            stageId: assessmentStage?.Id));
        if (assessmentStage != null)
        {
            _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
                service.Id,
                assessmentStage.Id,
                $"Stage assessment {decision}",
                assessorNote ?? $"Stage assessment decision: {decision}.",
                currentUserId.Value,
                actorName));
        }
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            assessment.Id,
            assessment.ServiceId,
            assessment.Decision,
            assessment.TaxAmount,
            assessment.DutyAmount,
            assessment.Notes,
            assessment.CreateAt,
            service.Status,
            WorkflowStageId = assessmentStage?.Id,
            StageStatus = assessmentStage?.Status
        });
    }

    [HttpGet("GetStageDiscussions/{serviceId:long}/{stageId:long}")]
    public async Task<IActionResult> GetStageDiscussions(long serviceId, long stageId)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();
        if (!await _context.ServiceStageExecutions.AnyAsync(stage =>
                stage.Id == stageId &&
                stage.ShipmentId == serviceId &&
                stage.RecordStatus == RecordStatus.Active &&
                stage.Shipment != null &&
                stage.Shipment.RecordStatus == RecordStatus.Active))
            return NotFound("Service stage not found.");

        var discussions = await _context.StageComments
            .AsNoTracking()
            .Where(comment =>
                comment.ServiceStageId == stageId &&
                comment.RecordStatus == RecordStatus.Active)
            .OrderBy(comment => comment.CreateAt)
            .Select(comment => new
            {
                comment.Id,
                comment.Comment,
                comment.CreateAt,
                Author = comment.CommentedByUser == null
                    ? null
                    : new
                    {
                        comment.CommentedByUser.Id,
                        comment.CommentedByUser.FirstName,
                        comment.CommentedByUser.LastName,
                        comment.CommentedByUser.Username
                    }
            })
            .ToListAsync();

        return HandleSuccessResponse(discussions);
    }

    [HttpPost("AddStageDiscussion")]
    public async Task<IActionResult> AddStageDiscussion([FromBody] AddStageDiscussionRequest request)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();
        if (request.ServiceId <= 0 || request.StageId <= 0 || string.IsNullOrWhiteSpace(request.Comment))
            return BadRequest("A service, stage, and comment are required.");
        if (!await _context.ServiceStageExecutions.AnyAsync(stage =>
                stage.Id == request.StageId &&
                stage.ShipmentId == request.ServiceId &&
                stage.RecordStatus == RecordStatus.Active &&
                stage.Shipment != null &&
                stage.Shipment.RecordStatus == RecordStatus.Active))
            return NotFound("Service stage not found.");

        var comment = StageComment.Create(
            request.Comment.Trim(),
            request.StageId,
            currentUserId.Value);
        _context.StageComments.Add(comment);
        await _context.SaveChangesAsync();

        return HandleSuccessResponse(new
        {
            comment.Id,
            comment.ServiceStageId,
            comment.Comment,
            comment.CreateAt
        });
    }

    [HttpGet("GetConsignmentDetails/{serviceId:long}")]
    public async Task<IActionResult> GetConsignmentDetails(long serviceId)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();

        var service = await _context.Shipments
            .AsNoTracking()
            .Where(shipment =>
                shipment.Id == serviceId &&
                shipment.RecordStatus == RecordStatus.Active)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.TrackingNumber,
                shipment.Description,
                shipment.Mode,
                shipment.ServiceType,
                shipment.RouteCategory,
                shipment.DeclaredValue,
                shipment.TaxCategory,
                shipment.CountryOfOrigin,
                shipment.Origin,
                shipment.Destination,
                Transports = _context.Transports
                    .Where(transport =>
                        transport.RecordStatus == RecordStatus.Active &&
                        transport.ServiceStageId.HasValue &&
                        _context.ServiceStageExecutions.Any(stage =>
                            stage.Id == transport.ServiceStageId.Value &&
                            stage.ShipmentId == shipment.Id &&
                            stage.RecordStatus == RecordStatus.Active))
                    .OrderBy(transport => transport.Id)
                    .Select(transport => new
                    {
                        transport.Id,
                        transport.FullName,
                        transport.PlateNumber,
                        transport.PhoneNumber,
                        transport.ProductAmount,
                        transport.ServiceStageId
                    })
                    .ToList(),
                Documents = shipment.Documents
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(document => document.CreateAt)
                    .Select(document => new
                    {
                        document.Id,
                        document.OriginalFileName,
                        document.DocumentType,
                        document.Description,
                        document.FileSizeBytes,
                        document.CreateAt,
                        document.ShipmentStageId
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync();

        return service == null
            ? NotFound("Service not found.")
            : HandleSuccessResponse(service);
    }

    [HttpPost("NotifyImporter")]
    public async Task<IActionResult> NotifyImporter([FromBody] NotifyImporterRequest request)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();
        if (request.ServiceId <= 0 ||
            string.IsNullOrWhiteSpace(request.Subject) ||
            string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("A service, subject, and message are required.");

        var service = await _context.Shipments
            .FirstOrDefaultAsync(shipment =>
                shipment.Id == request.ServiceId &&
                shipment.RecordStatus == RecordStatus.Active);
        if (service == null)
            return NotFound("Service not found.");

        var subject = request.Subject.Trim();
        var message = request.Message.Trim();
        var serviceMessage = ServiceMessage.Create(
            subject,
            message,
            MessageType.Direct,
            service.Id,
            currentUserId.Value,
            service.ImporterId,
            isUrgent: request.IsUrgent);
        _context.ServiceMessages.Add(serviceMessage);
        _context.Notifications.Add(Notification.Create(
            subject,
            message,
            NotificationType.Message,
            service.ImporterId,
            service.Id,
            isUrgent: request.IsUrgent));
        await _context.SaveChangesAsync();

        return HandleSuccessResponse(new
        {
            serviceMessage.Id,
            serviceMessage.ServiceId,
            serviceMessage.Subject,
            serviceMessage.Content,
            serviceMessage.CreatedDate,
            RecipientUserId = service.ImporterId
        });
    }

    [HttpPost("EscalateService")]
    public async Task<IActionResult> EscalateService([FromBody] EscalateServiceRequest request)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsAssessor(currentUserId.Value))
            return Forbid();
        if (request.ServiceId <= 0 || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("A service and escalation reason are required.");

        var service = await _context.Shipments
            .FirstOrDefaultAsync(shipment =>
                shipment.Id == request.ServiceId &&
                shipment.RecordStatus == RecordStatus.Active);
        if (service == null)
            return NotFound("Service not found.");

        var reason = request.Reason.Trim();
        var message = string.IsNullOrWhiteSpace(request.Notes)
            ? reason
            : $"{reason}{Environment.NewLine}{request.Notes.Trim()}";
        var serviceMessage = ServiceMessage.Create(
            "Service escalation",
            message,
            MessageType.System,
            service.Id,
            currentUserId.Value,
            isUrgent: true,
            priority: "High");
        _context.ServiceMessages.Add(serviceMessage);

        var managers = await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.Trim().ToLower() == "manager" ||
                 userRole.Role.Name.Trim().ToLower() == "superadmin" ||
                 userRole.Role.Name.Trim().ToLower() == "admin"))
            .Select(userRole => userRole.UserId)
            .Distinct()
            .ToListAsync();
        foreach (var managerId in managers)
        {
            _context.Notifications.Add(Notification.Create(
                "Service escalation",
                message,
                NotificationType.SystemAlert,
                managerId,
                service.Id,
                isUrgent: true));
        }

        await _context.SaveChangesAsync();

        return HandleSuccessResponse(new
        {
            serviceMessage.Id,
            ServiceId = service.Id,
            service.TrackingNumber,
            serviceMessage.Subject,
            serviceMessage.Content,
            serviceMessage.CreatedDate,
            NotifiedManagers = managers.Count
        });
    }

    private async Task<bool> IsAssessor(long userId, CancellationToken cancellationToken = default)
    {
        var allowedRoles = new[] { "assessor", "manager", "admin", "superadmin" };

        return await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                allowedRoles.Contains(userRole.Role.Name.Trim().ToLower()),
                cancellationToken);
    }

    private async Task<string> GetAssessorNameAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => new { item.FirstName, item.LastName, item.Username })
            .FirstOrDefaultAsync(cancellationToken);
        if (user == null)
            return $"Assessor {userId}";

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName)
            ? user.Username ?? $"Assessor {userId}"
            : fullName;
    }

    private static string CustomerDocumentLabel(DocumentType documentType) =>
        documentType switch
        {
            DocumentType.BusinessLicense => "Business License",
            DocumentType.ImportLicense => "Import License",
            DocumentType.TINCertificate => "TIN Certificate",
            DocumentType.LegalDocument => "Legal Document",
            _ => documentType.ToString()
        };

    private static string FullName(string? firstName, string? lastName) =>
        string.Join(" ", new[] { firstName, lastName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim()));

    private static string? NormalizeDecision(string? decision)
    {
        var normalized = new string((decision ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

        return normalized switch
        {
            "approve" or "approved" => "Approve",
            "reject" or "rejected" => "Reject",
            "requestchanges" or "returnforrevision" or "requirescustomeraction" => "RequestChanges",
            "needsreview" => "NeedsReview",
            "signoff" or "signedoff" => "SignOff",
            "verified" => "Verified",
            _ => null
        };
    }

    private string? ResolveCustomerDocumentPath(string filePath) => ResolveDocumentPath(filePath);

    private sealed record AssessmentDocumentRecord(
        long Id,
        long? CustomerId,
        long? ServiceId,
        string Reference,
        long? StageId,
        string DocumentType,
        string FileName,
        string DownloadUrl,
        string Status,
        DateTime? ExpiryDate,
        bool IsExpired,
        DateTime UploadedAt,
        string? VerifiedByName,
        DateTime? VerifiedAt,
        string? UploadedByName,
        bool IsVerified,
        long FileSizeBytes,
        string? Description,
        string Source);

    private sealed record AssessmentActivityRecord(
        string Id,
        string Type,
        string Reference,
        string Description,
        DateTime Timestamp,
        string Stage);

}
