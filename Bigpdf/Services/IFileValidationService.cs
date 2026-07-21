namespace Bigpdf.Services;

public interface IFileValidationService
{
    bool IsValidFileExtension(string fileName, string[] allowedExtensions);
    Task<bool> ValidateFileMagicBytesAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
    bool IsWithinSizeLimit(long fileSizeBytes, long maxSizeBytes = 100 * 1024 * 1024); // default 100MB
}
