using System.Collections.Generic;

namespace Bigpdf.Models
{
    public class JobRequest
    {
        public JobType Type { get; set; }
        public string? InputRelativePath { get; set; }
        public string? OutputName { get; set; }
        public IDictionary<string, string>? Parameters { get; set; }
    }
}
