namespace Bigpdf.Models
{
    public record PdfOperationResult(bool Success, string Message, string? OutputRelativePath = null);
}
