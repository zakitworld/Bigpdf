using Microsoft.AspNetCore.Hosting;

namespace Bigpdf.Services;

public static class UploadPaths
{
    public static string GetUploadsRoot(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "uploads");

    public static bool TryResolveUploadPath(IWebHostEnvironment env, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var uploadsRoot = Path.GetFullPath(GetUploadsRoot(env));
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["uploads/".Length..];

        if (normalized.Contains("..", StringComparison.Ordinal))
            return false;

        var candidate = Path.GetFullPath(Path.Combine(uploadsRoot, normalized));
        var uploadsPrefix = uploadsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(uploadsPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, uploadsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    public static string ToPublicUrl(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
            path = path["uploads/".Length..];

        return $"/uploads/{path}";
    }
}
