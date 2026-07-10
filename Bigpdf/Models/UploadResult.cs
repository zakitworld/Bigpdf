namespace Bigpdf.Models
{
    public record UploadResult(bool Success, string Message, string? Path = null);
}
