using Mapster;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using MohamedTransit.API.DTO.MOT.Request;
using MohamedTransit.API.DTO.MOT.Response;
using MohamedTransit.API.Helpers;
using MohamedTransit.API.Services;
using MohamedTransit.Application.DataSeeder;
using MohamedTransit.Application.DTO;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;


namespace MohamedTransit.API.Controllers.MOT;

[ApiController]
[Authorize]
[Route("api/v1/[controller]")]
public class DataEncoderController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IFileStorageService _fileStorageService;
    private readonly IServiceWorkflowInitializer _workflowInitializer;

    public DataEncoderController(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IFileStorageService fileStorageService,
        IServiceWorkflowInitializer workflowInitializer)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _fileStorageService = fileStorageService;
        _workflowInitializer = workflowInitializer;
    }

    /// <summary>
    /// Create a new customer
    /// </summary>
    [HttpPost("CreateCustomer")]
    public async Task<IActionResult> CreateCustomer(
        [FromBody] MohamedTransit.API.DTO.MasterData.Request.CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.UserId <= 0)
            return BadRequest("A valid customer user ID is required.");

        var isActiveCustomerUser = await _context.Users
            .AsNoTracking()
            .AnyAsync(user =>
                user.Id == request.UserId &&
                user.RecordStatus == RecordStatus.Active &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.RecordStatus == RecordStatus.Active &&
                    userRole.Role.Name.ToLower() == "customer"),
                cancellationToken);
        if (!isActiveCustomerUser)
            return BadRequest("The selected user is not an active customer account.");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(existing => existing.UserId == request.UserId, cancellationToken);
        if (customer is not null && customer.IsRegistrationComplete())
            return Conflict("A customer profile already exists for this user.");

        if (customer is null)
        {
            customer = CreateCustomerProfile(request, currentUserId.Value);
            _context.Customers.Add(customer);
        }
        else
        {
            UpdateCustomerProfile(customer, request);
        }
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.BusinessName,
            customer.TINNumber,
            customer.BusinessLicense,
            customer.BusinessAddress,
            customer.City,
            customer.State,
            customer.PostalCode,
            customer.ContactPerson,
            customer.ContactPhone,
            customer.ContactEmail,
            customer.BusinessType,
            customer.ImportLicense,
            customer.ImportLicenseExpiry,
            customer.UserId,
            customer.IsVerified
        });
    }

    [HttpPost("CreateCustomerWithDocuments")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateCustomerWithDocuments(
        [FromForm] MohamedTransit.API.DTO.MasterData.Request.CreateCustomerWithDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.UserId <= 0)
            return BadRequest("A valid customer user ID is required.");

        var isActiveCustomerUser = await _context.Users
            .AsNoTracking()
            .AnyAsync(user =>
                user.Id == request.UserId &&
                user.RecordStatus == RecordStatus.Active &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.RecordStatus == RecordStatus.Active &&
                    userRole.Role.Name.ToLower() == "customer"),
                cancellationToken);
        if (!isActiveCustomerUser)
            return BadRequest("The selected user is not an active customer account.");
        var customer = await _context.Customers
            .FirstOrDefaultAsync(existing => existing.UserId == request.UserId, cancellationToken);
        if (customer is not null && customer.IsRegistrationComplete())
            return Conflict("A customer profile already exists for this user.");

        var uploads = CustomerKycUploads(request);
        foreach (var upload in uploads)
        {
            if (upload.File is not null && !TryValidateKycFile(upload.File, upload.ExpiresAt, out var error))
                return BadRequest(error);
        }

        if (customer is null)
        {
            customer = CreateCustomerProfile(request, currentUserId.Value);
            _context.Customers.Add(customer);
        }
        else
        {
            UpdateCustomerProfile(customer, request);
        }
        await _context.SaveChangesAsync(cancellationToken);

        var documents = new List<CustomerDocument>();
        foreach (var upload in uploads.Where(upload => upload.File is not null))
        {
            var document = await StoreCustomerKycDocumentAsync(
                customer,
                currentUserId.Value,
                upload.File!,
                upload.DocumentType,
                upload.ExpiresAt);
            documents.Add(document);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.BusinessName,
            customer.TINNumber,
            customer.BusinessLicense,
            customer.ImportLicense,
            customer.UserId,
            documents = documents.Select(ToCustomerKycResponse)
        }, "Customer created successfully.");
    }

    [HttpPost("customers/{customerId:long}/documents")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadCustomerKycDocument(
        long customerId,
        [FromForm] IFormFile file,
        [FromForm] string documentType,
        [FromForm] DateTime? expiresAt,
        [FromForm] string? description,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var customer = await _context.Customers.FirstOrDefaultAsync(
            item => item.Id == customerId &&
                    item.RecordStatus == RecordStatus.Active &&
                    (!ownerId.HasValue || item.CreatedByDataEncoderId == ownerId.Value),
            cancellationToken);
        if (customer is null)
            return NotFound("Customer not found.");
        if (customer.IsVerified)
            return Conflict("Verified customer profiles cannot be changed through this endpoint.");
        if (!TryParseKycDocumentType(documentType, out var parsedDocumentType))
            return BadRequest("documentType must be Business License, Import License, TIN Certificate, Legal Document, or Other.");
        if (!TryValidateKycFile(file, expiresAt, out var validationError))
            return BadRequest(validationError);

        var document = await StoreCustomerKycDocumentAsync(
            customer,
            currentUserId.Value,
            file,
            parsedDocumentType,
            expiresAt,
            description);
        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(ToCustomerKycResponse(document), "KYC document uploaded successfully.");
    }

    private async Task<IActionResult> GetServiceListAsync(
        string? search,
        string? status,
        string? mode,
        string? riskChannel,
        bool? isDraft,
        string sortBy,
        string sortOrder,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (page < 1 || pageSize is < 1 or > 100)
            return BadRequest("page must be positive and pageSize must be between 1 and 100.");
        if (!string.Equals(sortBy, "updatedAt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sortBy, "eta", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sortBy, "slaDaysLeft", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sortBy, "sla", StringComparison.OrdinalIgnoreCase))
            return BadRequest("sortBy must be updatedAt, eta, or slaDaysLeft.");
        if (!string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase))
            return BadRequest("sortOrder must be asc or desc.");
        if (!string.IsNullOrWhiteSpace(mode) &&
            !mode.Equals("Unimodal", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("Multimodal", StringComparison.OrdinalIgnoreCase))
            return BadRequest("mode must be Unimodal or Multimodal.");
        if (!string.IsNullOrWhiteSpace(riskChannel) && !IsRiskChannel(riskChannel))
            return BadRequest("riskChannel must be Green, Yellow, Blue, or Red.");
        if (!string.IsNullOrWhiteSpace(status) && !IsSupportedServiceStatus(status))
            return BadRequest("status must be Draft, Submitted, Pending, UnderReview, Approved, InProgress, NeedsReview, Blocked, Completed, Rejected, or Cancelled.");

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var query = _context.Shipments
            .AsNoTracking()
            .Where(shipment =>
                shipment.RecordStatus == RecordStatus.Active &&
                (!ownerId.HasValue || shipment.CreatedByDataEncoderId == ownerId.Value));
        var draftCount = await query.CountAsync(
            shipment => shipment.Status == ShipmentStatus.Draft ||
                        shipment.WorkflowStatus.ToLower() == "draft",
            cancellationToken);

        if (isDraft.HasValue)
            query = isDraft.Value
                ? query.Where(shipment =>
                    shipment.Status == ShipmentStatus.Draft ||
                    shipment.WorkflowStatus.ToLower() == "draft")
                : query.Where(shipment =>
                    shipment.Status != ShipmentStatus.Draft &&
                    shipment.WorkflowStatus.ToLower() != "draft");

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statusValue = status.Trim();
            var normalizedStatus = statusValue.Replace(" ", string.Empty).ToLowerInvariant();
            query = normalizedStatus switch
            {
                "pending" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "pending" ||
                    shipment.Status == ShipmentStatus.Draft ||
                    shipment.Status == ShipmentStatus.Submitted),
                "inprogress" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "inprogress" ||
                    shipment.Status == ShipmentStatus.InProgress ||
                    shipment.Status == ShipmentStatus.Approved),
                "needsreview" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "needsreview" ||
                    shipment.Status == ShipmentStatus.UnderReview),
                "underreview" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "underreview" ||
                    shipment.Status == ShipmentStatus.UnderReview),
                "requirescustomeraction" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "requirescustomeraction"),
                "blocked" => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == "blocked"),
                _ when Enum.TryParse<ShipmentStatus>(statusValue, true, out var parsedStatus) =>
                    query.Where(shipment =>
                        shipment.Status == parsedStatus ||
                        shipment.WorkflowStatus.ToLower() == statusValue.ToLower()),
                _ => query.Where(shipment =>
                    shipment.WorkflowStatus.ToLower() == statusValue.ToLower())
            };
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            var isUnimodal = mode.Equals("Unimodal", StringComparison.OrdinalIgnoreCase);
            query = isUnimodal
                ? query.Where(shipment =>
                    shipment.ServiceType == ServiceType.Unimodal ||
                    (!shipment.ServiceType.HasValue && shipment.Mode == TransportMode.UniModalRoad))
                : query.Where(shipment =>
                    shipment.ServiceType == ServiceType.Multimodal ||
                    (!shipment.ServiceType.HasValue && shipment.Mode == TransportMode.MultiModalSeaRail));
        }

        if (!string.IsNullOrWhiteSpace(riskChannel))
        {
            var channel = riskChannel.Trim().ToLowerInvariant();
            query = query.Where(shipment =>
                shipment.ManagerRiskChannel.ToLower() == channel ||
                (shipment.ManagerRiskChannel == string.Empty &&
                 shipment.RiskLevel.HasValue &&
                 shipment.RiskLevel.Value.ToString().ToLower() == channel));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            var customerCodeId = ParseCustomerCodeId(search);
            query = query.Where(shipment =>
                shipment.ServiceCode.ToLower().Contains(term) ||
                shipment.TrackingNumber.ToLower().Contains(term) ||
                shipment.ReferenceNumber.ToLower().Contains(term) ||
                shipment.Title.ToLower().Contains(term) ||
                shipment.Description.ToLower().Contains(term) ||
                shipment.Importer.FirstName.ToLower().Contains(term) ||
                shipment.Importer.LastName.ToLower().Contains(term) ||
                (shipment.Importer.FirstName + " " + shipment.Importer.LastName).ToLower().Contains(term) ||
                shipment.Importer.Username.ToLower().Contains(term) ||
                _context.Customers.Any(customer =>
                    customer.UserId == shipment.ImporterId &&
                    customer.RecordStatus == RecordStatus.Active &&
                    (customer.BusinessName.ToLower().Contains(term) ||
                     (customerCodeId.HasValue && customer.Id == customerCodeId.Value))));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var ascending = sortOrder.Equals("asc", StringComparison.OrdinalIgnoreCase);
        var orderedQuery = sortBy.ToLowerInvariant() switch
        {
            "eta" => ascending
                ? query.OrderBy(shipment => shipment.ETA)
                : query.OrderByDescending(shipment => shipment.ETA),
            "sla" or "sladaysleft" => ascending
                ? query.OrderBy(shipment => shipment.SlaDeadline)
                : query.OrderByDescending(shipment => shipment.SlaDeadline),
            _ => ascending
                ? query.OrderBy(shipment => shipment.UpdatedAt ?? shipment.CreateAt)
                : query.OrderByDescending(shipment => shipment.UpdatedAt ?? shipment.CreateAt)
        };

        var rows = await orderedQuery
            .ThenBy(shipment => shipment.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
                    .Select(customer => new { customer.Id, customer.BusinessName })
                    .FirstOrDefault(),
                ImporterName = shipment.Importer.FirstName + " " + shipment.Importer.LastName
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var items = rows.Select(row => new
        {
            id = row.Id,
            serviceCode = ValueOr(row.ServiceCode, row.TrackingNumber),
            referenceNumber = ValueOr(row.ReferenceNumber, row.TrackingNumber),
            title = ValueOr(row.Title, row.Description),
            importerName = ValueOr(row.Customer?.BusinessName, row.ImporterName),
            customerCode = CustomerCode(row.Customer?.Id ?? row.ImporterId),
            mode = ResolveServiceMode(row.ServiceType, row.Mode),
            status = ResolveServiceStatus(row.WorkflowStatus, row.Status),
            riskChannel = ResolveRiskChannel(row.ManagerRiskChannel, row.RiskLevel),
            caseExecutorName = row.AssignedCaseExecutor is null
                ? null
                : FullName(row.AssignedCaseExecutor.FirstName, row.AssignedCaseExecutor.LastName),
            eta = row.ETA,
            slaDaysLeft = row.SlaDeadline.HasValue
                ? (int?)Math.Ceiling((row.SlaDeadline.Value - now).TotalDays)
                : null,
            updatedAt = row.UpdatedAt ?? row.CreateAt
        }).ToList();

        return Ok(new { totalCount, draftCount, page, pageSize, items });
    }

    private async Task<IActionResult> UploadStageDocumentAsync(
        long serviceId,
        long stageId,
        IFormFile file,
        string documentType,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (serviceId <= 0 || stageId <= 0)
            return BadRequest("Valid serviceId and stageId values are required.");
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty document file is required.");
        if (file.Length > 25 * 1024 * 1024)
            return BadRequest("File is too large. Maximum size is 25 MB.");
        if (!TryParseDocumentType(documentType, out var parsedDocumentType))
            return BadRequest($"Invalid documentType '{documentType}'. Provide a valid document type name, label, or numeric value.");

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

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var service = await _context.Shipments.FirstOrDefaultAsync(item =>
            item.Id == serviceId &&
            item.RecordStatus == RecordStatus.Active &&
            (!ownerId.HasValue || item.CreatedByDataEncoderId == ownerId.Value),
            cancellationToken);
        if (service is null)
            return NotFound("Service not found.");
        var stage = await _context.ServiceStageExecutions.FirstOrDefaultAsync(item =>
            item.Id == stageId &&
            item.ShipmentId == serviceId &&
            item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (stage is null)
            return NotFound("Service stage not found.");

        var actorName = await GetUserFullNameAsync(currentUserId.Value, cancellationToken);
        var originalFileName = Path.GetFileName(file.FileName);
        var filePath = await _fileStorageService.SaveFileAsync(file, "uploads/documents");
        var fileName = Path.GetFileName(filePath);
        var document = ServiceDocument.Create(
            fileName,
            filePath,
            originalFileName,
            extension,
            file.Length,
            mimeType,
            parsedDocumentType,
            serviceId,
            currentUserId.Value,
            stageId,
            description: documentType.Trim(),
            documentTypeLabel: DocumentTypeLabel(parsedDocumentType, documentType));
        _context.ServiceDocuments.Add(document);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            stageId,
            "Stage document uploaded",
            $"{documentType.Trim()} uploaded: {originalFileName}",
            currentUserId.Value,
            actorName));
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _fileStorageService.DeleteFileAsync(filePath);
            throw;
        }

        return CreatedAtAction(
            nameof(GetServiceWorkspace),
            new { idOrCode = serviceId.ToString() },
            new
            {
                id = document.Id,
                serviceId,
                stageId,
                documentType = documentType.Trim(),
                document.OriginalFileName,
                document.MimeType
            });
    }

    /// <summary>
    /// Get all customers created by the data encoder
    /// </summary>
    [HttpGet("GetAllCustomers/{recordStatus?}")]
    [HttpGet("Customers")]
    public async Task<IActionResult> GetAllCustomers(
        RecordStatus? recordStatus,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var customerQuery = _context.Customers
            .AsNoTracking()
            .Include(customer => customer.User)
            .Where(customer => !ownerId.HasValue ||
                               customer.CreatedByDataEncoderId == ownerId.Value);
        if (recordStatus.HasValue)
            customerQuery = customerQuery.Where(customer => customer.RecordStatus == recordStatus.Value);

        var customers = await customerQuery
            .OrderByDescending(customer => customer.CreateAt)
            .ToListAsync(cancellationToken);
        var customerUserIds = customers
            .Where(customer => customer.UserId.HasValue)
            .Select(customer => customer.UserId!.Value)
            .Distinct()
            .ToArray();
        var activeStatuses = new[]
        {
            ShipmentStatus.Submitted,
            ShipmentStatus.UnderReview,
            ShipmentStatus.Approved,
            ShipmentStatus.InProgress
        };
        var activeCasesByImporter = await _context.Shipments
            .Where(shipment =>
                customerUserIds.Contains(shipment.ImporterId) &&
                (!ownerId.HasValue || shipment.CreatedByDataEncoderId == ownerId.Value) &&
                shipment.RecordStatus == RecordStatus.Active &&
                activeStatuses.Contains(shipment.Status))
            .GroupBy(shipment => shipment.ImporterId)
            .Select(group => new { ImporterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ImporterId, item => item.Count, cancellationToken);

        var rolesList = customers.Adapt<List<CustomerDetail>>();
        for (var index = 0; index < customers.Count; index++)
        {
            var customer = customers[index];
            rolesList[index].RegisteredAt = customer.CreateAt;
            rolesList[index].ActiveCases =
                customer.UserId.HasValue && activeCasesByImporter.TryGetValue(customer.UserId.Value, out var count)
                    ? count
                    : 0;
        }

        return HandleSuccessResponse(rolesList);

    }
    /// <summary>
    /// Update customer information before approval
    /// </summary>
    [HttpPut("UpdateCustomer")]
    public async Task<IActionResult> UpdateCustomer(
        [FromBody] MohamedTransit.API.DTO.MOT.Request.UpdateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.Id <= 0)
            return BadRequest("A valid customer ID is required.");

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var customer = await _context.Customers
            .FirstOrDefaultAsync(c =>
                c.Id == request.Id &&
                (!ownerId.HasValue || c.CreatedByDataEncoderId == ownerId.Value) &&
                c.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (customer == null)
            return NotFound("Customer not found.");
        if (customer.IsVerified)
            return Conflict("Verified customer information cannot be changed.");


        customer.UpdateBusinessInfo(
            request.BusinessName,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail
        );

        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.BusinessName,
            customer.BusinessAddress,
            customer.City,
            customer.State,
            customer.PostalCode,
            customer.ContactPerson,
            customer.ContactPhone,
            customer.ContactEmail,
            customer.IsVerified
        });
    }
    // POST: api/v1/DataEncoder/services
    [HttpPost("services")]
    [HttpPost("Create services")]
    public async Task<IActionResult> CreateService([FromBody] CreateServiceRequestDto request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var (service, error) = await CreateDraftServiceAsync(
            request,
            currentUserId.Value,
            ownerId,
            cancellationToken);
        if (error is not null)
            return error;

        var createdService = service!;
        return Ok(new
        {
            success = true,
            message = "Service draft created successfully.",
            data = new
            {
                id = createdService.Id,
                serviceCode = createdService.ServiceCode,
                referenceNumber = createdService.ReferenceNumber,
                trackingNumber = createdService.TrackingNumber,
                status = createdService.Status.ToString()
            }
        });
    }
    [HttpPut("services/{id}/service-type")]
    public async Task<IActionResult> UpdateServiceType(
        long id,
        [FromBody] UpdateServiceTypeRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var service = await _context.Shipments.FirstOrDefaultAsync(
            shipment => shipment.Id == id &&
                        (!ownerId.HasValue ||
                         shipment.CreatedByDataEncoderId == ownerId.Value) &&
                        shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service == null)
            return NotFound("Service not found");
        if (service.Status != ShipmentStatus.Draft)
            return Conflict("Service details can only be updated before submission.");
        if (!Enum.IsDefined(typeof(ServiceType), request.ServiceType))
            return BadRequest("A valid service type is required.");

        var serviceTypeChanged = service.ServiceType != request.ServiceType;
        if (serviceTypeChanged)
        {
            var existingStages = await _context.ServiceStageExecutions
                .Where(stage =>
                    stage.ShipmentId == service.Id &&
                    stage.RecordStatus == RecordStatus.Active)
                .ToListAsync(cancellationToken);
            var stageIds = existingStages.Select(stage => stage.Id).ToArray();
            var hasStageData = stageIds.Length > 0 &&
                (await _context.StageDocuments.AnyAsync(
                    document => document.ServiceStageId.HasValue &&
                                stageIds.Contains(document.ServiceStageId.Value),
                    cancellationToken) ||
                 await _context.StageComments.AnyAsync(
                    comment => comment.ServiceStageId.HasValue &&
                               stageIds.Contains(comment.ServiceStageId.Value),
                    cancellationToken) ||
                 await _context.ServiceDocuments.AnyAsync(
                    document => document.ShipmentStageId.HasValue &&
                                stageIds.Contains(document.ShipmentStageId.Value),
                    cancellationToken) ||
                 await _context.Transports.AnyAsync(
                    transport => transport.ServiceStageId.HasValue &&
                                 stageIds.Contains(transport.ServiceStageId.Value),
                    cancellationToken));
            var hasProgress = existingStages.Any(stage =>
                stage.StageNumber <= 1
                    ? stage.Status is not (StageStatus.NotStarted or StageStatus.InProgress)
                    : stage.Status != StageStatus.NotStarted);

            if (hasStageData || hasProgress)
                return Conflict("The service type cannot be changed after workflow stage activity has started.");

            _context.ServiceStageExecutions.RemoveRange(existingStages);
        }

        service.UpdateServiceType(request.ServiceType);
        await _context.SaveChangesAsync(cancellationToken);
        await EnsureDraftWorkflowInitializedAsync(id, currentUserId.Value, cancellationToken);

        return HandleSuccessResponse(new { Success = true, Message = "Service type updated successfully." });
    }
    [HttpPost("services/{id}/documents")]
    public async Task<IActionResult> UploadServiceDocuments(
        long id,
        [FromForm] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var service = await _context.Shipments
            .FirstOrDefaultAsync(
                shipment => shipment.Id == id &&
                            (!ownerId.HasValue ||
                             shipment.CreatedByDataEncoderId == ownerId.Value) &&
                            shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (service == null)
            return NotFound("Service not found");
        if (service.Status != ShipmentStatus.Draft)
            return Conflict("Documents can only be added before service submission.");

        if (files == null || files.Count == 0)
            return BadRequest("No files uploaded.");

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".xlsx"
        };
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName);
            if (file.Length <= 0 ||
                file.Length > 25 * 1024 * 1024 ||
                !allowedExtensions.Contains(extension))
                return BadRequest("Each document must be a PDF, JPG, PNG, or XLSX file no larger than 25 MB.");
        }

        var actorName = await _context.Users
            .Where(user => user.Id == currentUserId.Value)
            .Select(user => user.FirstName + " " + user.LastName)
            .FirstAsync(cancellationToken);
        foreach (var file in files)
        {
            var filePath = await _fileStorageService.SaveFileAsync(file, "uploads/documents");
            var document = ServiceDocument.Create(
                Path.GetFileName(filePath),
                filePath,
                Path.GetFileName(file.FileName),
                Path.GetExtension(file.FileName),
                file.Length,
                file.ContentType,
                DocumentType.Other,
                id,
                currentUserId.Value,
                description: "Supporting document");
            _context.ServiceDocuments.Add(document);
            _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
                id,
                null,
                "Service document uploaded",
                $"Supporting document uploaded: {Path.GetFileName(file.FileName)}",
                currentUserId.Value,
                actorName.Trim()));
        }
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new { Success = true, Message = "Documents uploaded successfully." });
    }
    [HttpPost("services/{id}/submit")]
    public async Task<IActionResult> SubmitService(long id, CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var service = await _context.Shipments.FirstOrDefaultAsync(
            shipment => shipment.Id == id &&
                        (!ownerId.HasValue ||
                         shipment.CreatedByDataEncoderId == ownerId.Value) &&
                        shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service == null)
            return NotFound("Service not found");

        if (service.Status != ShipmentStatus.Draft)
            return Conflict("Only draft services can be submitted.");

        var stagesExist = await _context.ServiceStageExecutions
            .AnyAsync(stage => stage.ShipmentId == service.Id, cancellationToken);
        if (!stagesExist)
        {
            await EnsureDraftWorkflowInitializedAsync(service.Id, currentUserId.Value, cancellationToken);
        }

        service.Submit();
        service.UpdateDataEncoderWorkspace(
            ValueOr(service.ServiceCode, service.TrackingNumber),
            ValueOr(service.ReferenceNumber, service.TrackingNumber),
            ValueOr(service.Title, service.Description));
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new { Success = true, Message = "Service submitted successfully and locked for approval." });
    }

    // GET: api/v1/DataEncoder/services
    [HttpGet("services")]
    public Task<IActionResult> GetAllServices(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? mode = null,
        [FromQuery] string? riskChannel = null,
        [FromQuery] bool? isDraft = null,
        [FromQuery] string sortBy = "updatedAt",
        [FromQuery] string sortOrder = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        GetServiceListAsync(
            search, status, mode, riskChannel, isDraft, sortBy, sortOrder, page, pageSize,
            cancellationToken);

    [HttpGet("DraftServices")]
    [HttpGet("services/drafts")]
    public Task<IActionResult> GetDraftServices(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? mode = null,
        [FromQuery] string? riskChannel = null,
        [FromQuery] string sortBy = "updatedAt",
        [FromQuery] string sortOrder = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        GetServiceListAsync(
            search, status, mode, riskChannel, true, sortBy, sortOrder, page, pageSize,
            cancellationToken);

    [HttpGet("services/{idOrCode}")]
    public async Task<IActionResult> GetServiceWorkspace(
        string idOrCode,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var serviceQuery = _context.Shipments
            .AsNoTracking()
            .Include(item => item.Importer)
            .Include(item => item.AssignedCaseExecutor)
            .Include(item => item.AssignedAssessor)
            .Where(item =>
                item.RecordStatus == RecordStatus.Active &&
                (!ownerId.HasValue || item.CreatedByDataEncoderId == ownerId.Value));
        var service = long.TryParse(idOrCode, out var serviceId)
            ? await serviceQuery.FirstOrDefaultAsync(item => item.Id == serviceId, cancellationToken)
            : await serviceQuery.FirstOrDefaultAsync(item =>
                item.ServiceCode.ToLower() == idOrCode.ToLower() ||
                item.TrackingNumber.ToLower() == idOrCode.ToLower(),
                cancellationToken);
        if (service is null)
            return NotFound("Service not found.");
        await _workflowInitializer.EnsureInitializedAsync(service.Id, cancellationToken);

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

        var expectedStages = ServiceWorkflowInitializer.GetWorkflowStages(service);
        var stageRows = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Include(stage => stage.RequiredDocuments)
            .Where(stage =>
                stage.ShipmentId == service.Id &&
                stage.RecordStatus == RecordStatus.Active)
            .ToListAsync(cancellationToken);
        stageRows = stageRows
            .Where(stage => expectedStages.Any(definition => definition.Stage == stage.Stage))
            .OrderBy(stage => Array.FindIndex(expectedStages, definition => definition.Stage == stage.Stage))
            .ThenBy(stage => stage.Id)
            .ToList();
        var stageIds = stageRows.Select(stage => stage.Id).ToArray();

        var serviceDocuments = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.ShipmentId == service.Id &&
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
                CreatedAt = document.CreateAt,
                UploadedByName = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
            })
            .ToListAsync(cancellationToken);
        var legacyStageDocuments = await _context.StageDocuments
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
                CreatedAt = document.CreateAt,
                UploadedByName = document.UploadedByUser == null
                    ? string.Empty
                    : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
            })
            .ToListAsync(cancellationToken);
        var documents = serviceDocuments
            .Select(document => new DataEncoderDocumentRow(
                document.Id,
                document.StageId,
                document.OriginalFileName,
                document.DocumentTypeLabel ?? DocumentTypeName(document.DocumentType),
                document.MimeType,
                document.FileSizeBytes,
                document.CreatedAt,
                document.UploadedByName))
            .Concat(legacyStageDocuments.Select(document => new DataEncoderDocumentRow(
                document.Id,
                document.StageId,
                document.OriginalFileName,
                document.DocumentTypeLabel ?? DocumentTypeName(document.DocumentType),
                document.MimeType,
                document.FileSizeBytes,
                document.CreatedAt,
                document.UploadedByName)))
            .ToList();
        var discussionCounts = await _context.StageDiscussions
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == service.Id &&
                item.RecordStatus == RecordStatus.Active &&
                stageIds.Contains(item.StageId))
            .GroupBy(item => item.StageId)
            .Select(group => new { StageId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.StageId, item => item.Count, cancellationToken);
        var workspaceStages = stageRows.Select((stage, index) =>
        {
            var stageDocuments = documents
                .Where(document => document.StageId == stage.Id)
                .OrderByDescending(document => document.CreatedAt)
                .ToList();
            var requiredDocuments = stage.RequiredDocuments
                .Where(required => required.RecordStatus == RecordStatus.Active)
                .Select(required => required.DocumentType)
                .Select(documentType =>
                {
                    var matchingDocument = stageDocuments.FirstOrDefault(document =>
                        DocumentTypesMatch(document.DocumentType, documentType));
                    return new
                    {
                        documentType,
                        status = matchingDocument is null ? "Outstanding" : "Received",
                        documentId = matchingDocument?.Id
                    };
                })
                .ToList();
            var previousCompletedStage = stageRows
                .Take(index)
                .LastOrDefault(previous => previous.Status == StageStatus.Completed);

            return new
            {
                id = stage.Id,
                workflowStageId = stage.Id,
                stageId = stage.Id,
                stageNumber = stage.StageNumber > 0 ? stage.StageNumber : index + 1,
                name = StageName(stage.Stage),
                status = HumanizeStageStatus(stage.Status),
                riskChannel = string.IsNullOrWhiteSpace(stage.RiskChannel) ||
                              stage.RiskChannel.Equals("Unassigned", StringComparison.OrdinalIgnoreCase)
                    ? ResolveRiskChannel(service.ManagerRiskChannel, service.RiskLevel)
                    : stage.RiskChannel,
                stageNote = stage.StageNote ?? stage.Comments,
                createdAt = stage.CreateAt,
                updatedAt = stage.UpdatedAt,
                completedAt = stage.CompletedAt,
                completedByName = stage.CompletedByName,
                durationHours = DurationHours(stage.StartedAt, stage.CompletedAt),
                previousStageSummary = previousCompletedStage is null
                    ? null
                    : new
                    {
                        stageNumber = previousCompletedStage.StageNumber,
                        name = StageName(previousCompletedStage.Stage),
                        summary = previousCompletedStage.SummaryNote ??
                                  previousCompletedStage.StageNote ??
                                  previousCompletedStage.Comments,
                        completedAt = previousCompletedStage.CompletedAt,
                        completedByName = previousCompletedStage.CompletedByName
                    },
                requiredDocuments,
                documents = stageDocuments.Select(document => new
                {
                    id = document.Id,
                    fileName = document.OriginalFileName,
                    documentType = document.DocumentType,
                    mimeType = document.MimeType,
                    fileSizeBytes = document.FileSizeBytes,
                    uploadedAt = document.CreatedAt,
                    uploadedByName = document.UploadedByName
                }),
                discussionCount = discussionCounts.GetValueOrDefault(stage.Id)
            };
        }).ToList();

        var currentStage = workspaceStages.FirstOrDefault(stage => stage.status != "Completed");
        var completedCount = workspaceStages.Count(stage => stage.status == "Completed");
        var completionPercentage = workspaceStages.Count == 0
            ? service.CompletionPercentage
            : (int)Math.Round(100d * completedCount / workspaceStages.Count);
        var riskChannel = ResolveRiskChannel(service.ManagerRiskChannel, service.RiskLevel);
        var importerName = FullName(service.Importer.FirstName, service.Importer.LastName);
        var activityRows = await _context.ServiceActivityLogs
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == service.Id &&
                item.RecordStatus == RecordStatus.Active)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ActionTitle,
                item.Description,
                item.ActorName,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var activity = activityRows.Select(item => new
        {
            id = item.Id,
            title = HumanizeActivityText(item.ActionTitle),
            description = HumanizeActivityText(item.Description),
            actorName = item.ActorName,
            createdAt = item.CreatedAt
        }).ToList();
        var assignedStaff = new List<object>();
        if (service.AssignedCaseExecutorId.HasValue && service.AssignedCaseExecutor is not null)
            assignedStaff.Add(new
            {
                id = service.AssignedCaseExecutorId.Value,
                fullName = FullName(service.AssignedCaseExecutor.FirstName, service.AssignedCaseExecutor.LastName),
                roleTitle = "Senior Case Executor"
            });
        if (service.AssignedAssessorId.HasValue && service.AssignedAssessor is not null)
            assignedStaff.Add(new
            {
                id = service.AssignedAssessorId.Value,
                fullName = FullName(service.AssignedAssessor.FirstName, service.AssignedAssessor.LastName),
                roleTitle = "KYC Assessor"
            });

        return Ok(new
        {
            id = service.Id,
            serviceCode = ValueOr(service.ServiceCode, service.TrackingNumber),
            referenceNumber = ValueOr(service.ReferenceNumber, service.TrackingNumber),
            title = ValueOr(service.Title, service.Description),
            status = ResolveServiceStatus(service.WorkflowStatus, service.Status),
            riskChannel,
            mode = ResolveServiceMode(service.ServiceType, service.Mode),
            origin = service.Origin,
            destination = service.Destination,
            eta = service.ETA,
            currentStageNumber = currentStage?.stageNumber ?? workspaceStages.Count,
            totalStages = workspaceStages.Count,
            completionPercentage,
            importer = new
            {
                id = service.ImporterId,
                companyName = ValueOr(customer?.BusinessName, importerName),
                customerCode = CustomerCode(customer?.Id ?? service.ImporterId),
                tin = customer?.TINNumber ?? string.Empty,
                contactName = ValueOr(customer?.ContactPerson, importerName),
                contactPhone = ValueOr(customer?.ContactPhone, service.Importer.Phone),
                email = ValueOr(customer?.ContactEmail, service.Importer.Email)
            },
            assignedStaff,
            riskCompliance = new
            {
                channel = riskChannel,
                guidanceText = RiskGuidance(riskChannel)
            },
            recentActivity = activity,
            logistics = new
            {
                carrier = service.CarrierOrVessel,
                origin = service.Origin,
                destination = service.Destination,
                routeCategory = service.RouteCategory,
                eta = service.ETA,
                consignmentInfo = service.ConsignmentDetails
            },
            stages = workspaceStages
        });
    }

    [HttpPost("services/stage-documents")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadStageDocument(
        [FromForm] DataEncoderStageDocumentUploadRequest request,
        CancellationToken cancellationToken) =>
        await UploadStageDocumentAsync(
            request.ServiceId,
            request.StageId,
            request.File,
            request.DocumentType,
            cancellationToken);

    [HttpPost("services/{serviceId:long}/stages/{stageId:long}/documents")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public Task<IActionResult> UploadStageDocument(
        long serviceId,
        long stageId,
        [FromForm] IFormFile file,
        [FromForm] string documentType,
        CancellationToken cancellationToken) =>
        UploadStageDocumentAsync(serviceId, stageId, file, documentType, cancellationToken);

    [HttpGet("services/{serviceId:long}/stages/{stageId:long}/discussions")]
    public async Task<IActionResult> GetStageDiscussions(
        long serviceId,
        long stageId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var stageExists = await _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == stageId &&
            stage.ShipmentId == serviceId &&
            stage.RecordStatus == RecordStatus.Active,
            cancellationToken);
        var serviceExists = await _context.Shipments.AnyAsync(service =>
            service.Id == serviceId &&
            service.RecordStatus == RecordStatus.Active &&
            (!ownerId.HasValue || service.CreatedByDataEncoderId == ownerId.Value),
            cancellationToken);
        if (!stageExists || !serviceExists)
            return NotFound("Service stage not found.");

        var discussions = await _context.StageDiscussions
            .AsNoTracking()
            .Where(item =>
                item.ServiceId == serviceId &&
                item.StageId == stageId &&
                item.RecordStatus == RecordStatus.Active)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                id = item.Id,
                serviceId = item.ServiceId,
                stageId = item.StageId,
                authorUserId = item.AuthorUserId,
                authorName = item.AuthorName,
                authorRole = item.AuthorRole,
                message = item.Message,
                createdAt = item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(discussions);
    }

    [HttpPost("services/{serviceId:long}/stages/{stageId:long}/discussions")]
    public async Task<IActionResult> AddStageDiscussion(
        long serviceId,
        long stageId,
        [FromBody] DataEncoderStageDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("message is required.");

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var stageExists = await _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == stageId &&
            stage.ShipmentId == serviceId &&
            stage.RecordStatus == RecordStatus.Active,
            cancellationToken);
        var serviceExists = await _context.Shipments.AnyAsync(service =>
            service.Id == serviceId &&
            service.RecordStatus == RecordStatus.Active &&
            (!ownerId.HasValue || service.CreatedByDataEncoderId == ownerId.Value),
            cancellationToken);
        if (!stageExists || !serviceExists)
            return NotFound("Service stage not found.");

        var actorName = await GetUserFullNameAsync(currentUserId.Value, cancellationToken);
        var actorRole = await GetPrimaryRoleAsync(currentUserId.Value, cancellationToken);
        var message = request.Message.Trim();
        var discussion = new StageDiscussion
        {
            ServiceId = serviceId,
            StageId = stageId,
            AuthorUserId = currentUserId.Value,
            AuthorName = actorName,
            AuthorRole = actorRole,
            Message = message,
            CreatedAt = DateTime.UtcNow,
            RecordStatus = RecordStatus.Active
        };
        _context.StageDiscussions.Add(discussion);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            stageId,
            "Stage discussion added",
            message,
            currentUserId.Value,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            id = discussion.Id,
            serviceId,
            stageId,
            authorUserId = discussion.AuthorUserId,
            authorName = discussion.AuthorName,
            authorRole = discussion.AuthorRole,
            message = discussion.Message,
            createdAt = discussion.CreatedAt
        });
    }

    [HttpPost("services/{serviceId:long}/message-importer")]
    public async Task<IActionResult> MessageImporter(
        long serviceId,
        [FromBody] DataEncoderImporterMessageRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("message is required.");

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var service = await _context.Shipments.FirstOrDefaultAsync(item =>
            item.Id == serviceId &&
            item.RecordStatus == RecordStatus.Active &&
            (!ownerId.HasValue || item.CreatedByDataEncoderId == ownerId.Value),
            cancellationToken);
        if (service is null)
            return NotFound("Service not found.");

        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? "Service message"
            : request.Subject.Trim();
        var message = request.Message.Trim();
        var actorName = await GetUserFullNameAsync(currentUserId.Value, cancellationToken);
        _context.ServiceMessages.Add(ServiceMessage.Create(
            subject,
            message,
            MessageType.Direct,
            serviceId,
            currentUserId.Value,
            service.ImporterId));
        _context.Notifications.Add(Notification.Create(
            subject,
            message,
            NotificationType.Message,
            service.ImporterId,
            serviceId));
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            serviceId,
            null,
            "Message sent to importer",
            $"{subject}: {message}",
            currentUserId.Value,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new { serviceId, importerId = service.ImporterId, subject });
    }

    [HttpGet("DocumentTemplates")]
    [HttpGet("document-templates")]
    public async Task<IActionResult> GetDocumentTemplates(
        [FromQuery] string? serviceType,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (!string.IsNullOrWhiteSpace(serviceType) &&
            !TryParseServiceType(serviceType, out _))
            return BadRequest("serviceType must be Unimodal or Multimodal.");

        var serviceTypes = string.IsNullOrWhiteSpace(serviceType)
            ? new[] { ServiceType.Unimodal, ServiceType.Multimodal }
            : [TryParseServiceType(serviceType, out var parsed) ? parsed : ServiceType.Multimodal];
        var templates = serviceTypes.SelectMany(type =>
            ServiceWorkflowInitializer.GetWorkflowStages(type)
                .SelectMany((definition, index) => definition.RequiredDocuments.Select(documentType => new
                {
                    serviceType = type.ToString(),
                    stageNumber = index + 1,
                    stageName = StageName(definition.Stage),
                    documentType,
                    isRequired = true
                })));
        return Ok(templates);
    }

    [HttpPost("BulkUpload")]
    [HttpPost("services/bulk-upload")]
    public async Task<IActionResult> BulkUploadServices(
        [FromBody] List<CreateServiceRequestDto>? requests,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (requests is null || requests.Count == 0 || requests.Count > 100)
            return BadRequest("Provide between 1 and 100 services.");

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var createdServices = new List<object>();
        foreach (var request in requests)
        {
            var (service, error) = await CreateDraftServiceAsync(
                request,
                currentUserId.Value,
                ownerId,
                cancellationToken);
            if (error is not null)
                return error;

            createdServices.Add(new
            {
                id = service!.Id,
                serviceCode = service.ServiceCode,
                referenceNumber = service.ReferenceNumber,
                status = service.Status.ToString()
            });
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(new { createdCount = createdServices.Count, services = createdServices });
    }

    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    [HttpGet("documents")]
    public async Task<IActionResult> GetAllDocuments(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var documents = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.RecordStatus == RecordStatus.Active &&
                document.Shipment != null &&
                document.Shipment.RecordStatus == RecordStatus.Active &&
                (!ownerId.HasValue ||
                 document.Shipment.CreatedByDataEncoderId == ownerId.Value))
            .OrderByDescending(document => document.CreateAt)
            .Select(document => new
            {
                document.Id,
                document.OriginalFileName,
                document.DocumentType,
                document.DocumentTypeLabel,
                ServiceReference = document.Shipment!.TrackingNumber,
                document.ShipmentStageId,
                UploadedByFirstName = document.UploadedByUser == null
                    ? null
                    : document.UploadedByUser.FirstName,
                UploadedByLastName = document.UploadedByUser == null
                    ? null
                    : document.UploadedByUser.LastName,
                document.CreateAt,
                document.RecordStatus
            })
            .ToListAsync(cancellationToken);

        var documentDtos = documents.Select(document => new
        {
            id = document.Id,
            documentName = document.OriginalFileName,
            documentType = document.DocumentTypeLabel ?? DocumentTypeName(document.DocumentType),
            serviceReference = document.ServiceReference,
            stage = document.ShipmentStageId?.ToString() ?? "N/A",
            uploadedBy = string.IsNullOrWhiteSpace(document.UploadedByFirstName) &&
                         string.IsNullOrWhiteSpace(document.UploadedByLastName)
                ? "System"
                : $"{document.UploadedByFirstName} {document.UploadedByLastName}".Trim(),
            uploadedDate = document.CreateAt.ToString("dd MMM yyyy, HH:mm"),
            status = document.RecordStatus.ToString()
        });

        return Ok(new { Success = true, Data = documentDtos });
    }
    /// <summary>
    /// Get data encoder dashboard
    /// </summary>
    [HttpGet("GetDashboard")]
    [HttpGet("Dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var ownerId = await GetDataEncoderOwnerIdAsync(currentUserId.Value, cancellationToken);
        var now = DateTime.UtcNow;
        var serviceReviewStatuses = new[] { ShipmentStatus.Submitted, ShipmentStatus.UnderReview };

        var dashboard = new MohamedTransit.API.DTO.MOT.Response.DataEncoderDashboardResponse
        {
            TotalCustomersCreated = await _context.Customers.CountAsync(c =>
                (!ownerId.HasValue || c.CreatedByDataEncoderId == ownerId.Value) &&
                c.RecordStatus == RecordStatus.Active, cancellationToken),
            PendingCustomerApprovals = await _context.Customers.CountAsync(c =>
                (!ownerId.HasValue || c.CreatedByDataEncoderId == ownerId.Value) &&
                c.RecordStatus == RecordStatus.Active &&
                !c.IsVerified, cancellationToken),
            TotalServicesCreated = await _context.Shipments.CountAsync(s =>
                (!ownerId.HasValue || s.CreatedByDataEncoderId == ownerId.Value) &&
                s.RecordStatus == RecordStatus.Active, cancellationToken),
            PendingServiceApprovals = await _context.Shipments.CountAsync(s =>
                (!ownerId.HasValue || s.CreatedByDataEncoderId == ownerId.Value) &&
                s.RecordStatus == RecordStatus.Active &&
                s.Status == ShipmentStatus.Submitted, cancellationToken),
            DraftServices = await _context.Shipments.CountAsync(s =>
                (!ownerId.HasValue || s.CreatedByDataEncoderId == ownerId.Value) &&
                s.RecordStatus == RecordStatus.Active &&
                s.Status == ShipmentStatus.Draft, cancellationToken),
            DueWithin48Hours = await _context.Shipments.CountAsync(s =>
                (!ownerId.HasValue || s.CreatedByDataEncoderId == ownerId.Value) &&
                s.RecordStatus == RecordStatus.Active &&
                serviceReviewStatuses.Contains(s.Status) &&
                s.CreateAt > now.AddHours(-48) &&
                s.CreateAt <= now,
                cancellationToken)
        };

        // Get recent activities
        var recentCustomersList = await _context.Customers
            .AsNoTracking()
            .Where(c =>
                (!ownerId.HasValue || c.CreatedByDataEncoderId == ownerId.Value) &&
                c.RecordStatus == RecordStatus.Active)
            .OrderByDescending(c => c.CreateAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        dashboard.RecentCustomers = recentCustomersList.Adapt<List<CustomerDetail>>();

        dashboard.RecentServices = await _context.Shipments
            .AsNoTracking()
            .Where(s =>
                (!ownerId.HasValue || s.CreatedByDataEncoderId == ownerId.Value) &&
                s.RecordStatus == RecordStatus.Active)
            .OrderByDescending(s => s.CreateAt)
            .Take(5)
            .Select(service => new ShipmentDetail
            {
                Id = service.Id,
                ServiceNumber = service.TrackingNumber,
                ItemDescription = service.Description,
                RouteCategory = service.RouteCategory,
                DeclaredValue = service.DeclaredValue,
                TaxCategory = service.TaxCategory,
                CountryOfOrigin = service.CountryOfOrigin,
                Origin = service.Origin,
                Destination = service.Destination,
                ServiceType = service.ServiceType ?? ServiceType.Multimodal,
                Status = service.Status,
                RiskLevel = service.RiskLevel,
                CustomerId = service.ImporterId,
                ImporterName = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .Select(customer => customer.BusinessName)
                    .FirstOrDefault() ?? string.Empty,
                ImporterTinNumber = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .Select(customer => customer.TINNumber)
                    .FirstOrDefault() ?? string.Empty,
                AssignedCaseExecutorId = service.AssignedCaseExecutorId,
                AssignedAssessorId = service.AssignedAssessorId,
                RegisteredDate = service.CreateAt,
                DueAt = service.Status == ShipmentStatus.Submitted || service.Status == ShipmentStatus.UnderReview
                    ? service.CreateAt.AddHours(48)
                    : null,
                LastUpdateDate = service.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(dashboard);
    }

    private async Task<(Shipment? Service, IActionResult? Error)> CreateDraftServiceAsync(
        CreateServiceRequestDto request,
        long actorUserId,
        long? ownerId,
        CancellationToken cancellationToken)
    {
        if (request.CustomerId <= 0 ||
            string.IsNullOrWhiteSpace(request.ItemDescription) ||
            string.IsNullOrWhiteSpace(request.RouteCategory) ||
            request.DeclaredValue < 0 ||
            string.IsNullOrWhiteSpace(request.TaxCategory) ||
            string.IsNullOrWhiteSpace(request.CountryOfOrigin) ||
            !TryParseServiceType(request.ServiceType, out var serviceType))
            return (null, BadRequest("Customer, shipment details, a non-negative declared value, and serviceType (Unimodal or Multimodal) are required."));

        var customer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.Id == request.CustomerId &&
                (!ownerId.HasValue || item.CreatedByDataEncoderId == ownerId.Value) &&
                item.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (customer is null)
            return (null, NotFound("Customer not found for this data encoder."));
        if (!customer.UserId.HasValue)
            return (null, BadRequest("The selected customer is not linked to an importer account."));

        var description = request.ItemDescription.Trim();
        var service = Shipment.Create(
            $"MT-{Guid.NewGuid():N}",
            description,
            customer.UserId.Value,
            description,
            serviceType == ServiceType.Multimodal
                ? TransportMode.MultiModalSeaRail
                : TransportMode.UniModalRoad,
            serviceType == ServiceType.Multimodal ? HubLocation.Mojo : HubLocation.Adama,
            request.CountryOfOrigin.Trim(),
            request.RouteCategory.Trim(),
            actorUserId);
        service.UpdateServiceType(serviceType);
        service.UpdateStatus(ShipmentStatus.Draft);
        service.UpdateDetails(
            description,
            request.RouteCategory.Trim(),
            request.DeclaredValue,
            request.TaxCategory.Trim(),
            request.CountryOfOrigin.Trim(),
            null);
        service.SetCreatedByDataEncoder(actorUserId);
        service.AssignmentNotes = request.Notes?.Trim();

        _context.Shipments.Add(service);
        await _context.SaveChangesAsync(cancellationToken);
        service.UpdateDataEncoderWorkspace(
            $"SVC-{DateTime.UtcNow:yyyy}-{service.Id:D4}",
            service.TrackingNumber,
            description);
        await EnsureDraftWorkflowInitializedAsync(service.Id, actorUserId, cancellationToken);

        var actorName = await GetUserFullNameAsync(actorUserId, cancellationToken);
        _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
            service.Id,
            null,
            "Service created",
            $"Service draft created: {description}",
            actorUserId,
            actorName));
        await _context.SaveChangesAsync(cancellationToken);
        return (service, null);
    }

    private async Task EnsureDraftWorkflowInitializedAsync(
        long serviceId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        await _workflowInitializer.EnsureInitializedAsync(serviceId, cancellationToken);
        var firstStage = await _context.ServiceStageExecutions
            .Where(stage =>
                stage.ShipmentId == serviceId &&
                stage.RecordStatus == RecordStatus.Active)
            .OrderBy(stage => stage.StageNumber)
            .ThenBy(stage => stage.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (firstStage?.Status == StageStatus.NotStarted)
        {
            firstStage.UpdateStatus(StageStatus.InProgress, updatedByUserId: actorUserId);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<CustomerDocument> StoreCustomerKycDocumentAsync(
        Customer customer,
        long uploadedByUserId,
        IFormFile file,
        DocumentType documentType,
        DateTime? expiresAt,
        string? description = null)
    {
        var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
        var storedPath = await _fileStorageService.SaveFileAsync(
            file,
            Path.Combine("Uploads", "CustomerDocuments"),
            $"{Guid.NewGuid():N}{extension}");
        var document = CustomerDocument.Create(
            Path.GetFileName(storedPath),
            storedPath,
            Path.GetFileName(file.FileName),
            extension,
            file.Length,
            GetKycMimeType(extension)!,
            documentType,
            customer.Id,
            uploadedByUserId,
            description?.Trim(),
            isRequired: true,
            expiryDate: expiresAt);
        _context.CustomerDocuments.Add(document);
        return document;
    }

    private static bool TryValidateKycFile(IFormFile? file, DateTime? expiresAt, out string? error)
    {
        if (file is null || file.Length == 0)
        {
            error = "A non-empty KYC document file is required.";
            return false;
        }
        if (file.Length > 25 * 1024 * 1024)
        {
            error = "File is too large. Maximum size is 25 MB.";
            return false;
        }
        var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
        if (GetKycMimeType(extension) is null)
        {
            error = "Only PDF, JPG, JPEG, and PNG KYC files are supported.";
            return false;
        }
        if (expiresAt.HasValue && expiresAt.Value.Date < DateTime.UtcNow.Date)
        {
            error = "expiresAt cannot be in the past.";
            return false;
        }

        error = null;
        return true;
    }

    private static string? GetKycMimeType(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => null
    };

    private static IEnumerable<(IFormFile? File, DocumentType DocumentType, DateTime? ExpiresAt)> CustomerKycUploads(
        MohamedTransit.API.DTO.MasterData.Request.CreateCustomerWithDocumentsRequest request)
    {
        yield return (request.BusinessLicenseFile, DocumentType.BusinessLicense, request.BusinessLicenseExpiresAt);
        yield return (request.ImportLicenseFile, DocumentType.ImportLicense, request.ImportLicenseExpiry);
        yield return (request.TINCertificateFile, DocumentType.TINCertificate, request.TINCertificateExpiresAt);
        yield return (request.LegalDocumentFile, DocumentType.LegalDocument, request.LegalDocumentExpiresAt);
    }

    private static Customer CreateCustomerProfile(
        MohamedTransit.API.DTO.MasterData.Request.CreateCustomerRequest request,
        long createdByDataEncoderId) =>
        Customer.Create(
            request.BusinessName,
            request.TINNumber,
            request.BusinessLicense,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail,
            request.BusinessType,
            request.ImportLicense,
            request.ImportLicenseExpiry,
            request.UserId,
            createdByDataEncoderId);

    private static Customer CreateCustomerProfile(
        MohamedTransit.API.DTO.MasterData.Request.CreateCustomerWithDocumentsRequest request,
        long createdByDataEncoderId) =>
        Customer.Create(
            request.BusinessName,
            request.TINNumber,
            request.BusinessLicense,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail,
            request.BusinessType,
            request.ImportLicense,
            request.ImportLicenseExpiry,
            request.UserId,
            createdByDataEncoderId);

    private static void UpdateCustomerProfile(
        Customer customer,
        MohamedTransit.API.DTO.MasterData.Request.CreateCustomerRequest request) =>
        UpdateCustomerProfile(
            customer,
            request.BusinessName,
            request.TINNumber,
            request.BusinessLicense,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail,
            request.BusinessType,
            request.ImportLicense,
            request.ImportLicenseExpiry);

    private static void UpdateCustomerProfile(
        Customer customer,
        MohamedTransit.API.DTO.MasterData.Request.CreateCustomerWithDocumentsRequest request) =>
        UpdateCustomerProfile(
            customer,
            request.BusinessName,
            request.TINNumber,
            request.BusinessLicense,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail,
            request.BusinessType,
            request.ImportLicense,
            request.ImportLicenseExpiry);

    private static void UpdateCustomerProfile(
        Customer customer,
        string businessName,
        string tinNumber,
        string businessLicense,
        string businessAddress,
        string city,
        string state,
        string postalCode,
        string contactPerson,
        string contactPhone,
        string contactEmail,
        string businessType,
        string importLicense,
        DateTime? importLicenseExpiry) =>
        customer.UpdateRegistrationDetails(
            SubmittedOrExisting(businessName, customer.BusinessName),
            SubmittedOrExisting(tinNumber, customer.TINNumber),
            SubmittedOrExisting(businessLicense, customer.BusinessLicense),
            SubmittedOrExisting(businessAddress, customer.BusinessAddress),
            SubmittedOrExisting(city, customer.City),
            SubmittedOrExisting(state, customer.State),
            SubmittedOrExisting(postalCode, customer.PostalCode),
            SubmittedOrExisting(contactPerson, customer.ContactPerson),
            SubmittedOrExisting(contactPhone, customer.ContactPhone),
            SubmittedOrExisting(contactEmail, customer.ContactEmail),
            SubmittedOrExisting(businessType, customer.BusinessType),
            SubmittedOrExisting(importLicense, customer.ImportLicense),
            importLicenseExpiry ?? customer.ImportLicenseExpiry);

    private static string SubmittedOrExisting(string submitted, string existing) =>
        string.IsNullOrWhiteSpace(submitted) ? existing : submitted.Trim();

    private static object ToCustomerKycResponse(CustomerDocument document) => new
    {
        id = document.Id,
        documentType = document.DocumentType.ToString(),
        fileName = document.OriginalFileName,
        fileUrl = $"/api/v1/Assessor/DownloadCustomerDocument?documentId={document.Id}",
        status = document.VerificationStatus,
        expiresAt = document.ExpiryDate,
        isExpired = document.IsExpired,
        uploadedAt = document.CreateAt
    };

    private static bool TryParseKycDocumentType(string? raw, out DocumentType documentType)
    {
        var normalized = string.Concat((raw ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (int.TryParse(normalized, out var numericValue) &&
            Enum.IsDefined(typeof(DocumentType), numericValue))
        {
            documentType = (DocumentType)numericValue;
            return true;
        }

        documentType = normalized switch
        {
            "businesslicense" => DocumentType.BusinessLicense,
            "importlicense" => DocumentType.ImportLicense,
            "tincertificate" or "tin" => DocumentType.TINCertificate,
            "legaldocument" => DocumentType.LegalDocument,
            "other" => DocumentType.Other,
            _ => default
        };
        return normalized.Length > 0 && Enum.IsDefined(typeof(DocumentType), documentType);
    }

    private async Task<bool> IsDataEncoder(long userId, CancellationToken cancellationToken) =>
        await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "dataencoder" ||
                 userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"),
                cancellationToken);

    private async Task<long?> GetDataEncoderOwnerIdAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var canViewAll = await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"),
                cancellationToken);
        return canViewAll ? null : userId;
    }

    private async Task<string> GetUserFullNameAsync(long userId, CancellationToken cancellationToken)
    {
        var fullName = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.FirstName + " " + user.LastName)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(fullName) ? "Data Encoder" : fullName.Trim();
    }

    private async Task<string> GetPrimaryRoleAsync(long userId, CancellationToken cancellationToken)
    {
        var role = await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.UserId == userId &&
                userRole.Role.RecordStatus == RecordStatus.Active)
            .Select(userRole => userRole.Role.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(role) ? "DataEncoder" : role;
    }

    private static bool TryParseServiceType(string? raw, out ServiceType serviceType)
    {
        serviceType = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var value = raw.Trim();
        if (Enum.TryParse(value, true, out serviceType) &&
            Enum.IsDefined(typeof(ServiceType), serviceType))
            return true;
        return false;
    }

    private static bool TryParseDocumentType(string? raw, out DocumentType documentType)
    {
        documentType = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (Enum.TryParse(value, true, out documentType) &&
            Enum.IsDefined(typeof(DocumentType), documentType))
            return true;

        var normalized = NormalizeDocumentType(value);
        foreach (var candidate in Enum.GetValues<DocumentType>())
        {
            if (NormalizeDocumentType(candidate.ToString()) == normalized)
            {
                documentType = candidate;
                return true;
            }
        }

        documentType = normalized switch
        {
            "SUPPORTINGDOCUMENT" or "IMPORTPERMIT" or "TRANSITPERMIT" or "AMENDMENTREQUEST"
                => DocumentType.LegalDocument,
            "COMMERCIALINVOICE" or "PROFORMAINVOICE" => DocumentType.Invoice,
            "AIRWAYBILL" or "BILLOFLADING" => DocumentType.ArrivalPhoto,
            "DELIVERYNOTE" => DocumentType.DeliveryOrder,
            "CLEARANCECERTIFICATE" or "CLEARANCEREQUEST" or "EXITAUTHORIZATION"
                => DocumentType.ClearanceDocument,
            "PAYMENTRECEIPT" or "TAXPAYMENTRECEIPT" or "STORAGEPAYMENTRECEIPT"
                => DocumentType.BankReceipt,
            "TEMPERATURELOG" or "PACKINGLIST" or "RISKASSESSMENT" or "WAREHOUSESTATUSREPORT" or
                "VALUATIONWORKSHEET" or "EMERGENCYAUTHORIZATION"
                => DocumentType.Other,
            _ => default
        };
        return normalized is
            "SUPPORTINGDOCUMENT" or "IMPORTPERMIT" or "TRANSITPERMIT" or "AMENDMENTREQUEST" or
            "COMMERCIALINVOICE" or "PROFORMAINVOICE" or "AIRWAYBILL" or "BILLOFLADING" or
            "DELIVERYNOTE" or "CLEARANCECERTIFICATE" or "CLEARANCEREQUEST" or "EXITAUTHORIZATION" or
            "PAYMENTRECEIPT" or "TAXPAYMENTRECEIPT" or "STORAGEPAYMENTRECEIPT" or
            "TEMPERATURELOG" or "PACKINGLIST" or "RISKASSESSMENT" or "WAREHOUSESTATUSREPORT" or
            "VALUATIONWORKSHEET" or "EMERGENCYAUTHORIZATION";
    }

    private static string DocumentTypeName(DocumentType documentType) => documentType switch
    {
        DocumentType.Invoice => "Commercial invoice",
        DocumentType.BankReceipt => "Payment receipt",
        DocumentType.DeliveryOrder => "Delivery order",
        DocumentType.InspectionReport => "Inspection report",
        DocumentType.ClearanceDocument => "Clearance certificate",
        DocumentType.TransportLicense => "Transport license",
        DocumentType.ArrivalPhoto => "Air waybill",
        DocumentType.StoreReceipt => "Store receipt",
        DocumentType.LegalDocument => "Supporting document",
        _ => "Other"
    };

    private static string DocumentTypeLabel(DocumentType documentType, string raw) =>
        int.TryParse(raw.Trim(), out _) ? DocumentTypeName(documentType) : raw.Trim();

    private static bool DocumentTypesMatch(string uploadedType, string requiredType) =>
        NormalizeDocumentType(uploadedType) == NormalizeDocumentType(requiredType);

    private static string NormalizeDocumentType(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized switch
        {
            "LEGALDOCUMENT" or "IMPORTPERMIT" or "TRANSITPERMIT" or "AMENDMENTREQUEST"
                => "SUPPORTINGDOCUMENT",
            "INVOICE" or "PROFORMAINVOICE" => "COMMERCIALINVOICE",
            "ARRIVALPHOTO" or "BILLOFLADING" => "AIRWAYBILL",
            "BANKRECEIPT" or "TAXPAYMENTRECEIPT" or "STORAGEPAYMENTRECEIPT"
                => "PAYMENTRECEIPT",
            "CLEARANCEDOCUMENT" or "CLEARANCEREQUEST" or "EXITAUTHORIZATION"
                => "CLEARANCECERTIFICATE",
            "DELIVERYNOTE" => "DELIVERYORDER",
            _ => normalized
        };
    }

    private static bool IsSupportedServiceStatus(string status) =>
        new[]
        {
            "Draft", "Submitted", "Pending", "UnderReview", "Under Review", "Approved",
            "InProgress", "In Progress", "NeedsReview", "Needs Review", "Blocked",
            "Completed", "Rejected", "Cancelled", "RequiresCustomerAction",
            "Requires Customer Action"
        }.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase) ||
        Enum.TryParse<ShipmentStatus>(status.Trim(), true, out _);

    private static string ResolveServiceStatus(string? workflowStatus, ShipmentStatus status)
    {
        var value = string.IsNullOrWhiteSpace(workflowStatus) ? status.ToString() : workflowStatus.Trim();
        return value.Replace("InProgress", "In Progress", StringComparison.OrdinalIgnoreCase)
            .Replace("NeedsReview", "Needs Review", StringComparison.OrdinalIgnoreCase)
            .Replace("RequiresCustomerAction", "Requires Customer Action", StringComparison.OrdinalIgnoreCase)
            .Replace("UnderReview", "Under Review", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveServiceMode(ServiceType? serviceType, TransportMode mode) =>
        (serviceType ?? (mode == TransportMode.UniModalRoad
            ? ServiceType.Unimodal
            : ServiceType.Multimodal)).ToString();

    private static string ResolveRiskChannel(string? channel, RiskLevel? riskLevel) =>
        string.IsNullOrWhiteSpace(channel) ? riskLevel?.ToString() ?? "Unassigned" : channel.Trim();

    private static bool IsRiskChannel(string value) =>
        value.Equals("Green", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Yellow", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Blue", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Red", StringComparison.OrdinalIgnoreCase);

    private static string RiskGuidance(string channel) => channel.ToLowerInvariant() switch
    {
        "green" => "Low risk: continue standard processing and retain the supporting documents.",
        "yellow" => "Moderate risk: review declarations and supporting documents before advancing.",
        "blue" => "Document review required: verify the submitted records and record any discrepancy.",
        "red" => "High risk: pause progression, document the concern, and escalate for review.",
        _ => "Follow the applicable compliance review guidance."
    };

    private static string CustomerCode(long customerId) => $"CUS-{customerId:D6}";

    private static long? ParseCustomerCodeId(string value)
    {
        var trimmed = value.Trim();
        var suffix = trimmed.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..]
            : trimmed;
        return long.TryParse(suffix, out var id) && id > 0 ? id : null;
    }

    private static string FullName(string? firstName, string? lastName) =>
        string.Join(" ", new[] { firstName, lastName }.Where(name => !string.IsNullOrWhiteSpace(name)));

    private static string ValueOr(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value;

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
        ShipmentStage.Clearance => "Clearance",
        _ => stage.ToString()
    };

    private static string HumanizeStageStatus(StageStatus status) => status switch
    {
        StageStatus.NotStarted or StageStatus.Pending => "Not Started",
        StageStatus.InProgress => "In Progress",
        StageStatus.NeedsReview => "Needs Review",
        StageStatus.RequiresCustomerAction => "Requires Customer Action",
        _ => status.ToString()
    };

    private static string HumanizeActivityText(string value) =>
        value.Replace("InProgress", "In Progress", StringComparison.OrdinalIgnoreCase)
            .Replace("NeedsReview", "Needs Review", StringComparison.OrdinalIgnoreCase)
            .Replace("RequiresCustomerAction", "Requires Customer Action", StringComparison.OrdinalIgnoreCase)
            .Replace("UnderReview", "Under Review", StringComparison.OrdinalIgnoreCase)
            .Replace("NotStarted", "Not Started", StringComparison.OrdinalIgnoreCase)
            .Replace("AssessmentandTaxPayment", "Assessment & Tax Payment", StringComparison.OrdinalIgnoreCase)
            .Replace("ExitandStoragePayment", "Exit & Storage Payment", StringComparison.OrdinalIgnoreCase);

    private static double? DurationHours(DateTime? startedAt, DateTime? completedAt) =>
        startedAt.HasValue
            ? Math.Max(0, ((completedAt ?? DateTime.UtcNow) - startedAt.Value).TotalHours)
            : null;

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

    private sealed record DataEncoderDocumentRow(
        long Id,
        long StageId,
        string OriginalFileName,
        string DocumentType,
        string MimeType,
        long FileSizeBytes,
        DateTime CreatedAt,
        string UploadedByName);
}
