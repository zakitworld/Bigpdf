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
                _store.Update(job);

                try
                {
                    var outputFolder = request.OutputName ?? "converted-pages";

                    PdfOperationResult result = request.Type switch
                    {
                        JobType.PdfToJpg => await _processor.ConvertPdfToJpgAsync(
                            request.InputRelativePath!,
                            outputFolder,
                            ct),
                        JobType.CompressPdf => await _processor.CompressPdfAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "compressed.pdf",
                            ct),
                        JobType.OcrPdf => await _processor.OcrPdfAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "ocr-output.txt",
                            ct),
                        JobType.PdfToWord => await _processor.ConvertPdfToWordAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "converted.docx",
                            ct),
                        JobType.Merge => await _processor.MergeAsync(
                            (request.InputRelativePath ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                            request.OutputName ?? "merged.pdf",
                            ct),
                        JobType.Split => await RunSplitAsync(request, ct),
                        JobType.AddPageNumbers => await _processor.AddPageNumbersAsync(
                            request.InputRelativePath!,
                            request.OutputName ?? "numbered.pdf",
                            ct),
                        _ => new PdfOperationResult(false, $"Unsupported job type: {request.Type}")
                    };

                    job.Status = result.Success ? JobStatus.Completed : JobStatus.Failed;
                    job.Message = result.Message;
                    job.ResultPath = result.OutputRelativePath;
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
    }
}
