using Microsoft.EntityFrameworkCore;

using MediatR;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

using MohamedTransit.Application.Commands;
using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Service;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;



namespace MohamedTransit.Application.Handlers
{
    internal class UploadDocumentHandler : IRequestHandler<UploadDocumentCommand, OperationResult<StageDocument>>
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IWebHostEnvironment _environment;
        private ISession _session => _httpContextAccessor.HttpContext.Session;
        private readonly TokenHandlerService _tokenHandlerService;

        public UploadDocumentHandler(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            IWebHostEnvironment environment,
            TokenHandlerService tokenHandlerService)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _environment = environment;
            _tokenHandlerService = tokenHandlerService;
        }

        public async Task<OperationResult<StageDocument>> Handle(UploadDocumentCommand request, CancellationToken cancellationToken)
        {
            var result = new OperationResult<StageDocument>();

            try
            {
                long userId = request.UploadedByUserId ?? 0;

                if (userId <= 0)
                {
                    var userName = GetCurrentUserName() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(userName))
                    {
                        var existingUser = await _context.Users
                            .AsNoTracking()
                            .FirstOrDefaultAsync(u => u.Username == userName, cancellationToken);

                        if (existingUser != null)
                            userId = existingUser.Id;
                    }
                }

                if (userId <= 0)
                {
                    result.AddError(ErrorCode.ValidationError, "An authenticated uploader is required.");
                    return result;
                }

                // Validate ShipmentStage exists
                var Stages = await _context.Stages
                    .FindAsync(new object[] { request.StageId }, cancellationToken);

                if (Stages == null)
                {
                    result.AddError(ErrorCode.NotFound, "Shipment stage not found.");
                    return result;
                }

                if (Stages.ShipmentId != request.ShipmentId)
                {
                    result.AddError(ErrorCode.ValidationError, "ServiceId does not match the stage.");
                    return result;
                }

                // Save file to disk (can be replaced with cloud storage)
                var uploadsFolder = Path.Combine(
                    _environment.ContentRootPath,
                    "App_Data",
                    "CaseExecutorStageDocuments",
                    DateTime.UtcNow.ToString("yyyyMMdd"));
                Directory.CreateDirectory(uploadsFolder);

                var originalFileName = Path.GetFileName(request.File.FileName);
                var uniqueFileName = $"{Guid.NewGuid()}_{originalFileName}";
                var fullPath = Path.Combine(uploadsFolder, uniqueFileName);

                await using (var stream = new FileStream(
                    fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await request.File.CopyToAsync(stream, cancellationToken);
                }
                var filePath = Path.GetRelativePath(_environment.ContentRootPath, fullPath)
                    .Replace('\\', '/');

                // Create StageDocument using factory method
                var document = StageDocument.Create(
                    fileName: uniqueFileName,
                    filePath: filePath,
                    originalFileName: originalFileName,
                    fileExtension: Path.GetExtension(originalFileName),
                    fileSizeBytes: request.File.Length,
                    mimeType: request.File.ContentType,
                    documentType: request.DocumentType,
                    serviceStageId: request.StageId,
                    uploadedByUserId: userId,
                    description: request.Description,
                    documentTypeLabel: request.DocumentTypeLabel
                );

                _context.StageDocuments.Add(document);
                var actorName = await _context.Users
                    .Where(user => user.Id == userId)
                    .Select(user => (user.FirstName + " " + user.LastName).Trim())
                    .FirstAsync(cancellationToken);
                _context.ServiceActivityLogs.Add(ServiceActivityLog.Create(
                    request.ShipmentId,
                    request.StageId,
                    "Stage document uploaded",
                    $"{document.DocumentTypeLabel ?? document.DocumentType.ToString()} uploaded: {originalFileName}",
                    userId,
                    actorName));
                await _context.SaveChangesAsync(cancellationToken);

                result.Payload = document;
                result.Message = "Document uploaded successfully.";
            }
            catch (Exception ex)
            {
                result.AddError(ErrorCode.ServerError, ex.Message);
            }

            return result;
        }

        // Helper: get currently logged-in username from Bearer token
        private string GetCurrentUserName()
        {
            var authorizationHeader = _httpContextAccessor.HttpContext?.Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith("Bearer "))
            {
                return null; // No token available in request
            }

            var token = authorizationHeader.Substring("Bearer ".Length).Trim();
            var claims = _tokenHandlerService.GetClaims(token); // Use TokenHandlerService to get claims

            var userNameClaim = claims?.FirstOrDefault(c => c.Type == "userName");
            return userNameClaim?.Value; // Return the username or null if not found
        }
    }
}
