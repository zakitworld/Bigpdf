using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bigpdf.Services
{
    public class BackgroundWorker : BackgroundService
    {
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly ILogger<BackgroundWorker> _logger;

        public BackgroundWorker(IBackgroundTaskQueue taskQueue, ILogger<BackgroundWorker> logger)
        {
            _taskQueue = taskQueue;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var workItem = await _taskQueue.DequeueAsync(stoppingToken);
                if (workItem is null)
                {
                    _logger.LogWarning("Dequeued a null work item; skipping");
                    continue;
                }

                try
                {
                    await workItem(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in background work item");
                }
            }
        }
    }
}
