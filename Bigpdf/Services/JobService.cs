using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bigpdf.Models;
using Microsoft.Extensions.Logging;

namespace Bigpdf.Services
{
    public class JobService : IJobService
    {
        private readonly IBackgroundTaskQueue _queue;
        private readonly IPdfProcessor _processor;
        private readonly IJobStore _store;
        private readonly ILogger<JobService> _logger;

        public JobService(
            IBackgroundTaskQueue queue,
            IPdfProcessor processor,
            IJobStore store,
            ILogger<JobService> logger)
        {
            _queue = queue;
            _processor = processor;
            _store = store;
            _logger = logger;
        }

        public Task<JobInfo> EnqueueJobAsync(JobRequest request)
        {
            var job = new JobInfo { Type = request.Type };
            _store.Add(job);

            _queue.QueueBackgroundWorkItem(async ct =>
            {
                job.Status = JobStatus.Running;
                job.StartedAt = DateTime.UtcNow;
                job.ProgressPercent = 10;
                job.Message = $"Starting {GetJobLabel(request.Type)}...";
                _store.Update(job);

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(GetJobTimeout(request.Type));
                    var jobToken = timeoutCts.Token;

                    var outputFolder = string.IsNullOrWhiteSpace(request.OutputName) || request.OutputName == "pages"
                        ? "converted-pages"
                        : request.OutputName;

                    // Prefer a unique output folder for image renders so concurrent jobs don't collide.
                    if (request.Type == JobType.PdfToJpg)
                    {
                        var inputName = Path.GetFileNameWithoutExtension(request.InputRelativePath ?? "document");
                        outputFolder = Path.Combine("uploads", $"{inputName}-pages-{Guid.NewGuid():N}").Replace('\\', '/');
                    }

                    job.ProgressPercent = 25;
                    job.Message = $"Running {GetJobLabel(request.Type)}...";
                    _store.Update(job);

                    PdfOperationResult result = request.Type switch
                    {
                        JobType.PdfToJpg => await _processor.ConvertPdfToJpgAsync(
                            request.InputRelativePath!,
                            outputFolder,
                            jobToken),
                        JobType.CompressPdf => await _processor.CompressPdfAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "compressed.pdf",
                            jobToken),
                        JobType.OcrPdf => await _processor.OcrPdfAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "ocr-output.txt",
                            jobToken),
                        JobType.PdfToWord => await _processor.ConvertPdfToWordAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "converted.docx",
                            jobToken),
                        JobType.Merge => await _processor.MergeAsync(
                            (request.InputRelativePath ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                            request.OutputName ?? "merged.pdf",
                            jobToken),
                        JobType.Split => await RunSplitAsync(request, jobToken),
                        JobType.AddPageNumbers => await _processor.AddPageNumbersAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "numbered.pdf",
                            jobToken),
                        JobType.PptToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.PdfToPpt => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pptx",
                            request.OutputName ?? "converted.pptx",
                            jobToken),
                        JobType.ExcelToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.PdfToExcel => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "xlsx",
                            request.OutputName ?? "converted.xlsx",
                            jobToken),
                        JobType.WordToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.TxtToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.RtfToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.OdtToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.HtmlToPdf => await _processor.ConvertViaLibreOfficeAsync(
                            request.InputRelativePath!,
                            "pdf",
                            request.OutputName ?? "converted.pdf",
                            jobToken),
                        JobType.DeletePages => await RunDeletePagesAsync(request, jobToken),
                        JobType.RotatePdf => await RunRotatePdfAsync(request, jobToken),
                        JobType.ProtectPdf => await RunProtectPdfAsync(request, jobToken),
                        JobType.UnlockPdf => await RunUnlockPdfAsync(request, jobToken),
                        _ => new PdfOperationResult(false, $"Unsupported job type: {request.Type}")
                    };

                    job.Status = result.Success ? JobStatus.Completed : JobStatus.Failed;
                    job.Message = result.Message;
                    job.ResultPath = result.OutputRelativePath;
                    job.ProgressPercent = result.Success ? 100 : 0;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    job.Status = JobStatus.Failed;
                    job.Message = $"{GetJobLabel(request.Type)} timed out. The converter may be missing, blocked, or the file may be too large. Try a smaller file or check the server tool configuration at /admin/tools.";
                    job.ProgressPercent = 0;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job {JobId} failed", job.Id);
                    job.Status = JobStatus.Failed;
                    job.Message = ex.Message;
                }
                finally
                {
                    job.CompletedAt = DateTime.UtcNow;
                    _store.Update(job);
                }
            });

            return Task.FromResult(job);
        }

        public JobInfo? GetJob(string id) => _store.Get(id);

        public IEnumerable<JobInfo> ListJobs() => _store.List();

        private async Task<PdfOperationResult> RunSplitAsync(JobRequest request, CancellationToken cancellationToken)
        {
            var pagesValue = request.Parameters?.TryGetValue("pages", out var pagesText) == true
                ? pagesText
                : request.OutputName;

            if (!PageRangeParser.TryParse(pagesValue ?? string.Empty, out var pages, out var parseError))
                return new PdfOperationResult(false, parseError ?? "Invalid page range");

            return await _processor.SplitAsync(
                request.InputRelativePath!,
                pages,
                request.Parameters?.TryGetValue("output", out var output) == true ? output : "split.pdf",
                cancellationToken);
        }

        private async Task<PdfOperationResult> RunDeletePagesAsync(JobRequest request, CancellationToken cancellationToken)
        {
            var pagesValue = request.Parameters?.TryGetValue("pages", out var pagesText) == true ? pagesText : string.Empty;
            if (!PageRangeParser.TryParse(pagesValue, out var pages, out var parseError))
                return new PdfOperationResult(false, parseError ?? "Invalid page range");

            return await _processor.DeletePdfPagesAsync(
                request.InputRelativePath!,
                pages,
                request.Parameters?.TryGetValue("output", out var output) == true ? output : "deleted.pdf",
                cancellationToken);
        }

        private async Task<PdfOperationResult> RunRotatePdfAsync(JobRequest request, CancellationToken cancellationToken)
        {
            var angleText = request.Parameters?.TryGetValue("angle", out var angleVal) == true ? angleVal : "90";
            if (!int.TryParse(angleText, out var angle)) angle = 90;

            var pagesValue = request.Parameters?.TryGetValue("pages", out var pagesText) == true ? pagesText : string.Empty;
            var pages = new List<int>();
            if (!string.IsNullOrWhiteSpace(pagesValue) && pagesValue != "all")
            {
                if (!PageRangeParser.TryParse(pagesValue, out pages, out var parseError))
                    return new PdfOperationResult(false, parseError ?? "Invalid page range");
            }

            return await _processor.RotatePdfAsync(
                request.InputRelativePath!,
                angle,
                pages,
                request.Parameters?.TryGetValue("output", out var output) == true ? output : "rotated.pdf",
                cancellationToken);
        }

        private async Task<PdfOperationResult> RunProtectPdfAsync(JobRequest request, CancellationToken cancellationToken)
        {
            var password = request.Parameters?.TryGetValue("password", out var pwd) == true ? pwd : string.Empty;
            if (string.IsNullOrWhiteSpace(password)) return new PdfOperationResult(false, "Password cannot be empty");

            return await _processor.ProtectPdfAsync(
                request.InputRelativePath!,
                password,
                request.Parameters?.TryGetValue("output", out var output) == true ? output : "protected.pdf",
                cancellationToken);
        }

        private async Task<PdfOperationResult> RunUnlockPdfAsync(JobRequest request, CancellationToken cancellationToken)
        {
            var password = request.Parameters?.TryGetValue("password", out var pwd) == true ? pwd : string.Empty;
            return await _processor.UnlockPdfAsync(
                request.InputRelativePath!,
                password,
                request.Parameters?.TryGetValue("output", out var output) == true ? output : "unlocked.pdf",
                cancellationToken);
        }

        private static TimeSpan GetJobTimeout(JobType type) => type switch
        {
            JobType.PdfToWord => TimeSpan.FromMinutes(3),
            JobType.OcrPdf => TimeSpan.FromMinutes(4),
            JobType.PdfToJpg => TimeSpan.FromMinutes(3),
            JobType.CompressPdf => TimeSpan.FromMinutes(3),
            JobType.PptToPdf or JobType.PdfToPpt or JobType.ExcelToPdf or JobType.PdfToExcel or JobType.WordToPdf => TimeSpan.FromMinutes(3),
            JobType.TxtToPdf or JobType.RtfToPdf or JobType.OdtToPdf or JobType.HtmlToPdf => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(2)
        };

        private static string GetJobLabel(JobType type) => type switch
        {
            JobType.PdfToJpg => "PDF to JPG conversion",
            JobType.CompressPdf => "PDF compression",
            JobType.OcrPdf => "OCR",
            JobType.PdfToWord => "PDF to Word conversion",
            JobType.Merge => "PDF merge",
            JobType.Split => "PDF split",
            JobType.AddPageNumbers => "page numbering",
            JobType.PptToPdf => "PowerPoint to PDF conversion",
            JobType.PdfToPpt => "PDF to PowerPoint conversion",
            JobType.ExcelToPdf => "Excel to PDF conversion",
            JobType.PdfToExcel => "PDF to Excel conversion",
            JobType.WordToPdf => "Word to PDF conversion",
            JobType.TxtToPdf => "TXT to PDF conversion",
            JobType.RtfToPdf => "RTF to PDF conversion",
            JobType.OdtToPdf => "ODT to PDF conversion",
            JobType.HtmlToPdf => "HTML to PDF conversion",
            JobType.DeletePages => "page deletion",
            JobType.RotatePdf => "page rotation",
            JobType.ProtectPdf => "PDF encryption",
            JobType.UnlockPdf => "PDF decryption",
            _ => "job"
        };
    }
}
