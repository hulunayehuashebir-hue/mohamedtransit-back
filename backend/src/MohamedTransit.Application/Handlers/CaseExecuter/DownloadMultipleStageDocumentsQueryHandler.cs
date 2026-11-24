using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MediatR;

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Queries.Customer.CaseExecuter;
using MohamedTransit.Domain.Data;

namespace MohamedTransit.Application.Handlers;

internal class DownloadMultipleStageDocumentsQueryHandler
    : IRequestHandler<DownloadMultipleStageDocumentsQuery, OperationResult<byte[]>>
{
    private readonly ApplicationDbContext _context;
    private readonly IWebHostEnvironment _environment;

    public DownloadMultipleStageDocumentsQueryHandler(
        ApplicationDbContext context,
        IWebHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    public async Task<OperationResult<byte[]>> Handle(
        DownloadMultipleStageDocumentsQuery request,
        CancellationToken cancellationToken)
    {
        var result = new OperationResult<byte[]>();

        try
        {
            if (request.DocumentIds == null || !request.DocumentIds.Any())
            {
                result.AddError(ErrorCode.NotFound, "No document IDs provided.");
                return result;
            }

            var documents = await _context.StageDocuments
                .AsNoTracking()
                .Where(d => request.DocumentIds.Contains(d.Id))
                .ToListAsync(cancellationToken);

            if (!documents.Any())
            {
                result.AddError(ErrorCode.NotFound, "No documents found in database.");
                return result;
            }

            using var ms = new MemoryStream();
            int addedFilesCount = 0;

            // ZipArchive scope ውስጥ በመክተት መጨረሻ ላይ ራሱ Flush/Dispose ማድረጉን ማረጋገጥ
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var doc in documents)
                {
                    var filePath = string.IsNullOrEmpty(doc.FilePath)
                        ? null
                        : ResolveDocumentPath(doc.FilePath);
                    if (filePath == null)
                        continue;

                    var entry = zip.CreateEntry(doc.FileName ?? Path.GetFileName(filePath));
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(filePath);

                    await fileStream.CopyToAsync(entryStream, cancellationToken);
                    addedFilesCount++;
                }
            } // እዚህ ጋር ZipArchive ይዘጋል፤ ዳታው ወደ MemoryStream ተጽፎ ያበቃል።

            if (addedFilesCount == 0)
            {
                result.AddError(ErrorCode.NotFound, "Physical files were not found on the server.");
                return result;
            }

            result.Payload = ms.ToArray();
            return result;
        }
        catch (Exception ex)
        {
            result.AddError(ErrorCode.ServerError, ex.Message);
            return result;
        }
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
