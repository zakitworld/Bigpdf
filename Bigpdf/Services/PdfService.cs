using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Bigpdf.Services
{
    public class PdfService : IPdfService
    {
        private readonly string _uploadsFolder;
        private readonly ILogger<PdfService> _logger;

        public PdfService(IWebHostEnvironment env, ILogger<PdfService> logger)
        {
            _uploadsFolder = UploadPaths.GetUploadsRoot(env);
            _logger = logger;
            if (!Directory.Exists(_uploadsFolder))
            {
                Directory.CreateDirectory(_uploadsFolder);
            }
        }

        public async Task<string?> SaveFileAsync(Stream stream, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            try
            {
                var safeName = Path.GetFileName(fileName) ?? "upload";
                var finalName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{safeName}";
                var targetPath = Path.Combine(_uploadsFolder, finalName);

                await using (var fs = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fs, 81920, cancellationToken).ConfigureAwait(false);
                }

                return Path.Combine("uploads", finalName).Replace('\\', '/');
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save uploaded file {FileName}", fileName);
                return null;
            }
        }

        public Task<IEnumerable<string>> ListFilesAsync()
        {
            if (!Directory.Exists(_uploadsFolder))
            {
                return Task.FromResult(Enumerable.Empty<string>());
            }

            var files = Directory.EnumerateFiles(_uploadsFolder)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .OrderByDescending(n => n)
                .ToList();

            return Task.FromResult<IEnumerable<string>>(files);
        }

        public Task<bool> DeleteFileAsync(string fileName)
        {
            try
            {
                var targetPath = Path.Combine(_uploadsFolder, Path.GetFileName(fileName));
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file {FileName}", fileName);
                return Task.FromResult(false);
            }
        }
    }
}
