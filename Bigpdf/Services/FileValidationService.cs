namespace Bigpdf.Services;

public class FileValidationService : IFileValidationService
{
    private static readonly Dictionary<string, List<byte[]>> MagicBytes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".pdf", new List<byte[]> { new byte[] { 0x25, 0x50, 0x44, 0x46 } } }, // %PDF
        { ".png", new List<byte[]> { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } } },
        { ".jpg", new List<byte[]> { new byte[] { 0xFF, 0xD8, 0xFF } } },
        { ".jpeg", new List<byte[]> { new byte[] { 0xFF, 0xD8, 0xFF } } },
        { ".webp", new List<byte[]> { new byte[] { 0x52, 0x49, 0x46, 0x46 } } }, // RIFF
        { ".docx", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } }, // PK.. (ZIP header)
        { ".xlsx", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } },
        { ".pptx", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } },
        { ".odt", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } },
        { ".ods", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } },
        { ".odp", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } },
        { ".zip", new List<byte[]> { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } }
    };

    public bool IsValidFileExtension(string fileName, string[] allowedExtensions)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return allowedExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> ValidateFileMagicBytesAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !MagicBytes.TryGetValue(ext, out var signatures))
        {
            // If we don't have magic bytes configured (e.g. .txt), allow if stream is readable
            return true;
        }

        var maxHeaderLen = signatures.Max(s => s.Length);
        var buffer = new byte[maxHeaderLen];
        
        var originalPosition = stream.CanSeek ? stream.Position : 0;
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, maxHeaderLen), cancellationToken);

        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        if (bytesRead == 0) return false;

        foreach (var sig in signatures)
        {
            if (bytesRead >= sig.Length && buffer.Take(sig.Length).SequenceEqual(sig))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsWithinSizeLimit(long fileSizeBytes, long maxSizeBytes = 100 * 1024 * 1024)
    {
        return fileSizeBytes > 0 && fileSizeBytes <= maxSizeBytes;
    }
}
