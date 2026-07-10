using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bigpdf.Services
{
    public interface IPdfService
    {
        /// <summary>
        /// Save an uploaded file stream to the server and return the relative path (or null on failure).
        /// </summary>
        Task<string?> SaveFileAsync(Stream stream, string fileName, string contentType, CancellationToken cancellationToken = default);

        /// <summary>
        /// List saved uploaded file names.
        /// </summary>
        Task<IEnumerable<string>> ListFilesAsync();

        /// <summary>
        /// Delete an uploaded file by its filename.
        /// </summary>
        Task<bool> DeleteFileAsync(string fileName);
    }
}
