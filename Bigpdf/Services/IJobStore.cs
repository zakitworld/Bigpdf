using System.Collections.Generic;
using Bigpdf.Models;

namespace Bigpdf.Services;

public interface IJobStore
{
    void Add(JobInfo job);
    void Update(JobInfo job);
    JobInfo? Get(string id);
    IEnumerable<JobInfo> List();
}
