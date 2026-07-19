using Bigpdf.Models;

namespace Bigpdf.Services;

public interface IPdfService
{
    Task<PdfOperationResult> CompressPdfAsync(string filePath, int quality = 85);
    Task<PdfOperationResult> MergePdfsAsync(IEnumerable<string> filePaths);
    Task<PdfOperationResult> SplitPdfAsync(string filePath, string pageRanges);
    Task<PdfOperationResult> AddWatermarkAsync(string filePath, string watermarkText, float opacity = 0.6f);
    Task<PdfOperationResult> ConvertToImagesAsync(string filePath, string outputFormat = "jpg");
    Task<PdfOperationResult> AddPageNumbersAsync(string filePath);
    Task<PdfOperationResult> PerformOcrAsync(string filePath);
    Task<PdfOperationResult> ConvertOfficeToPdfAsync(string filePath);
}