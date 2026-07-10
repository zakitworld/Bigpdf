using System.Collections.Concurrent;
using System.Text.Json;
using Bigpdf.Models;
using Microsoft.AspNetCore.Hosting;

namespace Bigpdf.Services;

public class JobStore : IJobStore
{
    private static readonly TimeSpan JobRetention = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, JobInfo> _jobs = new();
    private readonly string _filePath;
    private readonly ILogger<JobStore> _logger;
    private readonly object _saveLock = new();

    public JobStore(IWebHostEnvironment env, ILogger<JobStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "jobs.json");
        Load();
    }

    public void Add(JobInfo job)
    {
        _jobs[job.Id] = job;
        Save();
    }

    public void Update(JobInfo job)
    {
        _jobs[job.Id] = job;
        Save();
    }

    public JobInfo? Get(string id)
    {
        _jobs.TryGetValue(id, out var job);
        return job;
    }

    public IEnumerable<JobInfo> List()
    {
        return _jobs.Values.OrderByDescending(j => j.CreatedAt);
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var jobs = JsonSerializer.Deserialize<List<JobInfo>>(json);
            if (jobs is null)
                return;

            var cutoff = DateTime.UtcNow - JobRetention;
            foreach (var job in jobs)
            {
                if (job.CompletedAt is not null && job.CompletedAt < cutoff
                    && job.Status is JobStatus.Completed or JobStatus.Failed)
                {
                    continue;
                }

                _jobs[job.Id] = job;
            }

            Save();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load jobs from {FilePath}", _filePath);
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var jobs = _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();
                var json = JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save jobs to {FilePath}", _filePath);
            }
        }
    }
}
