using Microsoft.EntityFrameworkCore;

using MediatR;
using Microsoft.AspNetCore.Hosting;

using MohamedTransit.Application.Helper;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;
using MohamedTransit.Application.Queries.Customer.CaseExecuter;
namespace MohamedTransit.Application.Handlers;
internal class DownloadStageDocumentQueryHandler
    : IRequestHandler<DownloadStageDocumentQuery, OperationResult<StageDocument>>
{
    private readonly ApplicationDbContext _context;
    private readonly IWebHostEnvironment _environment;

    public DownloadStageDocumentQueryHandler(
        ApplicationDbContext context,
        IWebHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    public async Task<OperationResult<StageDocument>> Handle(
        DownloadStageDocumentQuery request,
        CancellationToken cancellationToken)
    {
        var result = new OperationResult<StageDocument>();

        // Fetch document from DB
        var document = await _context.StageDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId, cancellationToken);

        if (document == null)
        {
            result.AddError(ErrorCode.NotFound, "Document not found in database");
            return result;
        }

        // Verify file exists on disk
        if (string.IsNullOrEmpty(document.FilePath) || ResolveDocumentPath(document.FilePath) == null)
        {
            result.AddError(ErrorCode.NotFound, "File not found on server");
            return result;
        }

        result.Payload = document;
        result.Message = "Document ready for download";

        return result;
    }

    private string? ResolveDocumentPath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
            return File.Exists(filePath) ? filePath : null;

        var webRoot = _environment.WebRootPath ??
            Path.Combine(_environment.ContentRootPath, "wwwroot");
        var candidates = new[]
        {
            Path.Combine(webRoot, filePath),
            Path.Combine(_environment.ContentRootPath, filePath)
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
