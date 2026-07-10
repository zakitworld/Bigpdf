using System.Threading.Tasks;
using Bigpdf.Models;

namespace Bigpdf.Services
{
    public interface IJobService
    {
        Task<JobInfo> EnqueueJobAsync(JobRequest request);
        JobInfo? GetJob(string id);
        System.Collections.Generic.IEnumerable<JobInfo> ListJobs();
    }
}
