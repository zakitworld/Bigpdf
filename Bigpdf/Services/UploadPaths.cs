using Microsoft.Extensions.FileProviders;

namespace Bigpdf.Services;

public static class UploadPaths
{
    public static string GetUploadsRoot(IWebHostEnvironment env)
    {
        var uploadsPath = Path.Combine(env.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsPath);
        return uploadsPath;
    }

    public static bool TryResolveUploadPath(IWebHostEnvironment env, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var uploadsRoot = GetUploadsRoot(env);
            var combined = Path.Combine(uploadsRoot, relativePath);

            // Security: Prevent path traversal
            if (!combined.StartsWith(uploadsRoot, StringComparison.Ordinal))
                return false;

            fullPath = Path.GetFullPath(combined);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToPublicUrl(string relativePath)
    {
        return $"/uploads/{relativePath.Replace('\\', '/')}";
    }
}