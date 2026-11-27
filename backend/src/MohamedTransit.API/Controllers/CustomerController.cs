using Mapster;

using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MohamedTransit.API.DTO.MasterData.Request;
using MohamedTransit.API.DTO.MOT.Request;
using MohamedTransit.API.DTO.MOT.Response;
using MohamedTransit.API.Helpers;
using MohamedTransit.API.Services;
using MohamedTransit.Application.Commands;
using MohamedTransit.Application.Commands.Shipment;
using MohamedTransit.Application.Queries.Customer;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

using MohameTransit.API.DTO.MOT.Request;
namespace MohamedTransit.API.Controllers.MOT;
[ApiController]
[Route("api/v1/[controller]")]
public class CustomerController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IWebHostEnvironment _environment;
    private readonly IFileStorageService _fileStorageService;

    public CustomerController(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IWebHostEnvironment environment,
        IFileStorageService fileStorageService)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _environment = environment;
        _fileStorageService = fileStorageService;
    }

    /// <summary>
    /// Create a new service request as a customer
    /// </summary>
    [HttpPost("CreateService")]
    public async Task<IActionResult> CreateService([FromBody] CreateCustomerServiceRequest request)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        // Verify customer exists and is verified
        var customer = await _context.Customers
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.UserId == currentUserId.Value && c.IsVerified);

        if (customer == null)
            return BadRequest("Customer not found or not verified");

        var command = new CreateServiceCommand(
          customer.Id,
          currentUserId.Value,
          request.Description,
          (TransportMode)request.Mode, 
          request.Origin,
          request.Destination
      );
        var result = await _mediator.Send(command);

        // ErrorOr ስለሌለ በቀጥታ ውጤቱን እንመልሳለን
        return HandleSuccessResponse(result);
    }
    [HttpPost("Register")]
    public async Task<IActionResult> RegisterAsCustomer(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        // 1. አሁን ሎጊን ያደረገውን ዩዘር ID ከ Token እናገኛለን
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        // 2. ይህ ዩዘር ቀደም ብሎ እንደ Customer ተመዝግቦ እንደሆነ እናረጋግጣለን
        var existingCustomer = await _context.Customers
            .FirstOrDefaultAsync(c => c.UserId == currentUserId.Value, cancellationToken);

        if (existingCustomer is not null && existingCustomer.IsRegistrationComplete())
            return Conflict("You are already registered as a customer.");

        var customer = existingCustomer is null
            ? CreateCustomerFromRegistration(request, currentUserId.Value)
            : existingCustomer;

        if (existingCustomer is null)
            _context.Customers.Add(customer);
        else
            UpdateCustomerFromRegistration(customer, request);
        await _context.SaveChangesAsync(cancellationToken);

        var message = existingCustomer is null
            ? "Customer registered successfully. Waiting for admin approval."
            : "Customer registration details updated successfully. Waiting for admin approval.";
        return HandleSuccessResponse(customer, message);
    }

    [HttpPost("RegisterWithDocuments")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> RegisterAsCustomerWithDocuments(
        [FromForm] CreateCustomerWithDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId is null)
            return Unauthorized("User not authenticated");

        var uploads = GetKycUploads(request);
        foreach (var upload in uploads)
        {
            if (upload.File is not null && !TryValidateKycFile(upload.File, upload.ExpiresAt, out var error))
                return BadRequest(error);
        }

        var existingCustomer = await _context.Customers.FirstOrDefaultAsync(
            customer => customer.UserId == currentUserId.Value,
            cancellationToken);
        if (existingCustomer is not null && existingCustomer.IsRegistrationComplete())
            return Conflict("You are already registered as a customer.");

        var customer = existingCustomer ?? CreateCustomerFromRegistration(request, currentUserId.Value);
        if (existingCustomer is null)
            _context.Customers.Add(customer);
        else
            UpdateCustomerFromRegistration(customer, request);
        await _context.SaveChangesAsync(cancellationToken);

        foreach (var upload in uploads.Where(upload => upload.File is not null))
        {
            await StoreKycDocumentAsync(
                customer,
                currentUserId.Value,
                upload.File!,
                upload.DocumentType,
                upload.ExpiresAt,
                cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(customer, "Customer registered successfully. Waiting for admin approval.");
    }

    [HttpPost("UploadKycDocument")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadKycDocument(
        [FromForm] IFormFile file,
        [FromForm] string documentType,
        [FromForm] DateTime? expiresAt,
        [FromForm] string? description,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!TryParseKycDocumentType(documentType, out var parsedDocumentType))
            return BadRequest("documentType must be Business License, Import License, TIN Certificate, Legal Document, or Other.");
        if (file == null || file.Length == 0)
            return BadRequest("A non-empty KYC document file is required.");
        if (file.Length > 25 * 1024 * 1024)
            return BadRequest("File is too large. Maximum size is 25 MB.");

        var customer = await _context.Customers.FirstOrDefaultAsync(item =>
                item.UserId == currentUserId.Value &&
                item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (customer == null)
            return NotFound("Register a customer profile before uploading KYC documents.");
        if (customer.IsVerified)
            return Conflict("Verified customer profiles cannot be changed through this endpoint.");

        if (!TryValidateKycFile(file, expiresAt, out var validationError))
            return BadRequest(validationError);

        var document = await StoreKycDocumentAsync(
            customer,
            currentUserId.Value,
            file,
            parsedDocumentType,
            expiresAt,
            cancellationToken,
            description);
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(ToKycDocumentResponse(document), "KYC document uploaded successfully.");
    }

    [HttpPost("~/api/v1/Customers/{customerId:long}/documents")]
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

        var customer = await _context.Customers.FirstOrDefaultAsync(
            item => item.Id == customerId && item.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (customer is null)
            return NotFound("Customer not found.");

        var hasStaffAccess = await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == currentUserId.Value &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "dataencoder" ||
                 userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"),
                cancellationToken);
        if (customer.UserId != currentUserId.Value && !hasStaffAccess)
            return Forbid();
        if (customer.IsVerified)
            return Conflict("Verified customer profiles cannot be changed through this endpoint.");
        if (!TryParseKycDocumentType(documentType, out var parsedDocumentType))
            return BadRequest("documentType must be Business License, Import License, TIN Certificate, Legal Document, or Other.");
        if (!TryValidateKycFile(file, expiresAt, out var validationError))
            return BadRequest(validationError);

        var document = await StoreKycDocumentAsync(
            customer,
            currentUserId.Value,
            file,
            parsedDocumentType,
            expiresAt,
            cancellationToken,
            description);
        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(ToKycDocumentResponse(document), "KYC document uploaded successfully.");
    }

    private async Task<CustomerDocument> StoreKycDocumentAsync(
        Customer customer,
        long uploadedByUserId,
        IFormFile file,
        DocumentType documentType,
        DateTime? expiresAt,
        CancellationToken cancellationToken,
        string? description = null)
    {
        var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
        var mimeType = GetKycMimeType(extension);
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
            mimeType!,
            documentType,
            customer.Id,
            uploadedByUserId,
            description?.Trim(),
            isRequired: true,
            expiryDate: expiresAt);
        _context.CustomerDocuments.Add(document);
        return document;
    }

    private static object ToKycDocumentResponse(CustomerDocument document) => new
    {
        id = document.Id,
        documentType = KycDocumentLabel(document.DocumentType),
        fileName = document.OriginalFileName,
        fileUrl = $"/api/v1/Assessor/DownloadCustomerDocument?documentId={document.Id}",
        status = document.VerificationStatus,
        expiresAt = document.ExpiryDate,
        isExpired = document.IsExpired,
        uploadedAt = document.CreateAt
    };

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

    private static IEnumerable<(IFormFile? File, DocumentType DocumentType, DateTime? ExpiresAt)> GetKycUploads(
        CreateCustomerWithDocumentsRequest request)
    {
        yield return (request.BusinessLicenseFile, DocumentType.BusinessLicense, request.BusinessLicenseExpiresAt);
        yield return (request.ImportLicenseFile, DocumentType.ImportLicense, request.ImportLicenseExpiry);
        yield return (request.TINCertificateFile, DocumentType.TINCertificate, request.TINCertificateExpiresAt);
        yield return (request.LegalDocumentFile, DocumentType.LegalDocument, request.LegalDocumentExpiresAt);
    }

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

    private static string KycDocumentLabel(DocumentType documentType) => documentType switch
    {
        DocumentType.BusinessLicense => "Business License",
        DocumentType.ImportLicense => "Import License",
        DocumentType.TINCertificate => "TIN Certificate",
        DocumentType.LegalDocument => "Legal Document",
        _ => documentType.ToString()
    };
    /// <summary>
    /// Get all services for the current customer
    /// </summary>
    [HttpGet("GetMyServices")]
    public async Task<IActionResult> GetMyServices([FromQuery] long UserId, RecordStatus? recordStatus)
    {
        var query = new GetMyServicesQuery { CustomerId = UserId, RecordStatus = recordStatus };
        var result = await _mediator.Send(query);
        var rolesList = result.Payload.Adapt<List<ShipmentDetail>>();
        return result.IsError ? HandleErrorResponse(result.Errors) : HandleSuccessResponse(rolesList);
    }

    /// <summary>
    /// Upload document for a service stage
    /// <summary>
    /// Upload document for a service stage
    /// </summary>
    [HttpPost("UploadStageDocument")]
    public async Task<IActionResult> UploadStageDocument(
        [FromForm] long serviceId,
        [FromForm] long stageId,
        [FromForm] IFormFile file,
        [FromForm] DocumentType documentType)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

       
        var service = await _context.Shipments
     .FirstOrDefaultAsync(s => s.Id == serviceId); 

        if (service == null)
            return NotFound("Service not found"); 

        var stage = await _context.ShipmentStages
            .FirstOrDefaultAsync(s => s.Id == stageId && s.ShipmentId == serviceId);

        if (stage == null)
            return NotFound("Service stage not found");

        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded");

        // Save file
        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "service-documents");
        Directory.CreateDirectory(uploadsFolder);

        var uniqueFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        using (var fileStream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(fileStream);
        }

        // Create document record
        var document = StageDocument.Create(
            uniqueFileName,
            Path.Combine("service-documents", uniqueFileName),
            file.FileName,
            Path.GetExtension(file.FileName),
            file.Length,
            file.ContentType,
            documentType,
            stageId,
            currentUserId.Value
        );

        _context.StageDocuments.Add(document);
        await _context.SaveChangesAsync();

        return HandleSuccessResponse(document);
    }
    /// <summary>
    /// Get customer notifications
    /// </summary>
    [HttpGet("GetNotifications")]
    public async Task<IActionResult> GetNotifications([FromQuery] bool unreadOnly = false)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        var query = _context.Notifications
            .Include(n => n.Shipment)
            .Where(n => n.UserId == currentUserId.Value);

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        var notifications = await query
            .OrderByDescending(n => n.CreateAt)
            .ToListAsync();

        return HandleSuccessResponse(notifications);
    }

    /// <summary>
    /// Mark notification as read
    /// </summary>
    [HttpPut("MarkNotificationAsRead")]
    public async Task<IActionResult> MarkNotificationAsRead([FromQuery] long notificationId)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == currentUserId.Value);

        if (notification == null)
            return NotFound("Notification not found");

        notification.MarkAsRead();
        await _context.SaveChangesAsync();

        return HandleSuccessResponse(notification);
    }

    /// <summary>
    /// Get customer profile information
    /// </summary>
    [HttpGet("GetProfile")]
    public async Task<IActionResult> GetProfile()
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        var customer = await _context.Customers
            .Include(c => c.User)
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.UserId == currentUserId.Value);

        if (customer == null)
            return NotFound("Customer profile not found");

        return HandleSuccessResponse(customer);
    }
    /// <summary>
    /// Update service inspection type
    /// </summary>
    [HttpPut("UpdateInspectionType")] // ወይም [HttpGet] እንደ ጥያቄዎ ዓይነት
    public async Task<IActionResult> UpdateInspectionType([FromQuery] long id, [FromQuery] int InspectionTypes)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        // 1. Shipment-ውን በ id እና በ customer (currentUserId) ይፈልጉ
        var service = await _context.Shipments
            .FirstOrDefaultAsync(s => s.Id == id && s.ImporterId == currentUserId.Value);

        if (service == null)
            return NotFound("Service or Shipment not found");

        // 2. የኢንስፔክሽን ዓይነቱን መቀየር (የእርስዎን ሜቶድ ወይም ፊልድ ስም ይጠቀሙ)
        // service.UpdateInspectionType((InspectionType)InspectionTypes); 
        // 👆 የኢንቲጀር ዋጋውን ወደሚፈለገው Enum ከቀየሩት በኋላ እዚህ ጋር ያዘምኑት

        await _context.SaveChangesAsync();

        return HandleSuccessResponse(service);
    }

    /// <summary>
    /// Update customer profile
    /// </summary>
    [HttpPut("UpdateProfile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateCustomerProfileRequest request)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(c => c.UserId == currentUserId.Value);

        if (customer == null)
            return NotFound("Customer profile not found");

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

        await _context.SaveChangesAsync();

        return HandleSuccessResponse(customer);
    }

    private static Customer CreateCustomerFromRegistration(
        CreateCustomerRequest request,
        long userId) =>
        CreateCustomerFromRegistration(
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
            userId);

    private static Customer CreateCustomerFromRegistration(
        CreateCustomerWithDocumentsRequest request,
        long userId) =>
        CreateCustomerFromRegistration(
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
            userId);

    private static Customer CreateCustomerFromRegistration(
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
        DateTime? importLicenseExpiry,
        long userId) =>
        Customer.Create(
            businessName,
            tinNumber,
            businessLicense,
            businessAddress,
            city,
            state,
            postalCode,
            contactPerson,
            contactPhone,
            contactEmail,
            businessType,
            importLicense,
            importLicenseExpiry,
            userId,
            userId);

    private static void UpdateCustomerFromRegistration(
        Customer customer,
        CreateCustomerRequest request) =>
        UpdateCustomerFromRegistration(
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

    private static void UpdateCustomerFromRegistration(
        Customer customer,
        CreateCustomerWithDocumentsRequest request) =>
        UpdateCustomerFromRegistration(
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

    private static void UpdateCustomerFromRegistration(
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
        DateTime? importLicenseExpiry)
    {
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
    }

    private static string SubmittedOrExisting(string submitted, string existing) =>
        string.IsNullOrWhiteSpace(submitted) ? existing : submitted.Trim();

}
