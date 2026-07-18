using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bigpdf.Models;

namespace Bigpdf.Services
{
    public interface IPdfProcessor
    {
        Task<PdfOperationResult> MergeAsync(IEnumerable<string> inputRelativePaths, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> SplitAsync(string inputRelativePath, IEnumerable<int> pagesOneBased, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> ConvertJpgToPdfAsync(IEnumerable<string> inputImageRelativePaths, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> ConvertPdfToJpgAsync(string inputRelativePath, string outputFolderRelative, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> AddWatermarkAsync(string inputRelativePath, string watermarkText, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> AddPageNumbersAsync(string inputRelativePath, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> CompressPdfAsync(string inputRelativePath, string outputFileName, CancellationToken cancellationToken = default);

        // Advanced operations (stubs)
        Task<PdfOperationResult> OcrPdfAsync(string inputRelativePath, string outputTextFileRelative, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> ConvertPdfToWordAsync(string inputRelativePath, string outputRelativePath, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> ConvertViaLibreOfficeAsync(string inputRelativePath, string targetExtension, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> DeletePdfPagesAsync(string inputRelativePath, IEnumerable<int> pagesToDelete, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> RotatePdfAsync(string inputRelativePath, int angle, IEnumerable<int> pagesToRotate, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> ProtectPdfAsync(string inputRelativePath, string password, string outputFileName, CancellationToken cancellationToken = default);

        Task<PdfOperationResult> UnlockPdfAsync(string inputRelativePath, string password, string outputFileName, CancellationToken cancellationToken = default);
    }
}
