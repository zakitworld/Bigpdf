using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bigpdf.Services;

public class FileCleanupService : BackgroundService
{
    private readonly ILogger<FileCleanupService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _maxAge = TimeSpan.FromHours(24);

    public FileCleanupService(ILogger<FileCleanupService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File Cleanup Background Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var uploadsDir = UploadPaths.GetUploadsRoot(_env);
                CleanOldFiles(uploadsDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during background file cleanup.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private void CleanOldFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        var dirInfo = new DirectoryInfo(folderPath);
        var cutoff = DateTime.UtcNow.Subtract(_maxAge);

        foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    file.Delete();
                    _logger.LogInformation("Deleted expired file: {FilePath}", file.FullName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not delete file {FilePath}: {Message}", file.FullName, ex.Message);
            }
        }
    }
}
