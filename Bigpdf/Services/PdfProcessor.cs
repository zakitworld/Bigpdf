using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bigpdf.Models;
using Microsoft.AspNetCore.Hosting;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Drawing;

namespace Bigpdf.Services
{
    using Microsoft.Extensions.Configuration;

    public class PdfProcessor : IPdfProcessor
    {
        private readonly string _uploadsFolder;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

        public PdfProcessor(IWebHostEnvironment env, IConfiguration configuration)
        {
            _env = env;
            _configuration = configuration;
            _uploadsFolder = UploadPaths.GetUploadsRoot(env);
            if (!Directory.Exists(_uploadsFolder)) Directory.CreateDirectory(_uploadsFolder);
        }

        private string? GetConfiguredToolPath(string key)
        {
            // 1) Environment variables — accept both KEY and KE_Y forms
            //    e.g. GhostscriptPath → GHOSTSCRIPTPATH and GHOSTSCRIPT_PATH
            foreach (var envName in GetEnvironmentVariableNames(key))
            {
                try
                {
                    var fromEnv = Environment.GetEnvironmentVariable(envName);
                    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim().Trim('"');
                }
                catch { }
            }

            // 2) appsettings (Tools:Key)
            try
            {
                var cfg = _configuration?.GetSection("Tools")?[key];
                if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim().Trim('"');
            }
            catch { }

            // 3) local toolpaths.json in content root (Admin → /admin/tools)
            try
            {
                var file = Path.Combine(_env.ContentRootPath, "toolpaths.json");
                if (File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty(key, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim().Trim('"');
                    }
                }
            }
            catch { }

            return null;
        }

        private static IEnumerable<string> GetEnvironmentVariableNames(string key)
        {
            var upper = key.ToUpperInvariant();
            yield return upper;

            // Insert underscores before capitals: GhostscriptPath → GHOSTSCRIPT_PATH
            var withUnderscores = System.Text.RegularExpressions.Regex
                .Replace(key, "(?<!^)([A-Z])", "_$1")
                .ToUpperInvariant();
            if (!string.Equals(withUnderscores, upper, StringComparison.Ordinal))
                yield return withUnderscores;
        }

        private static IEnumerable<string> GetKnownGhostscriptPaths()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "gs"),
            };

            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var exe in new[] { "gswin64c.exe", "gswin32c.exe", "gs.exe" })
                    {
                        var candidate = Path.Combine(versionDir, "bin", exe);
                        if (File.Exists(candidate))
                            yield return candidate;
                    }
                }
            }
        }

        private static IEnumerable<string> GetKnownLibreOfficePaths()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    yield return candidate;
            }
        }

        private async Task<string?> ResolveExecutableAsync(
            string configKey,
            IEnumerable<string> pathCandidates,
            IEnumerable<string> pathNames,
            string versionArgs,
            CancellationToken cancellationToken)
        {
            var configured = GetConfiguredToolPath(configKey);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured) || configured.IndexOfAny(new[] { '/', '\\', ':' }) < 0)
                    return configured;
            }

            foreach (var candidate in pathCandidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            foreach (var name in pathNames)
            {
                try
                {
                    var probe = await RunProcessAsync(name, versionArgs, TimeSpan.FromSeconds(8), cancellationToken);
                    if (probe.ExitCode == 0)
                        return name;
                }
                catch { }
            }

            return null;
        }

        private Task<string?> ResolveGhostscriptAsync(CancellationToken cancellationToken) =>
            ResolveExecutableAsync(
                "GhostscriptPath",
                GetKnownGhostscriptPaths(),
                new[] { "gswin64c.exe", "gswin32c.exe", "gs", "gswin64c", "gswin32c" },
                "--version",
                cancellationToken);

        private Task<string?> ResolveLibreOfficeAsync(CancellationToken cancellationToken) =>
            ResolveExecutableAsync(
                "LibreOfficePath",
                GetKnownLibreOfficePaths(),
                new[] { "soffice.exe", "soffice", "libreoffice" },
                "--version",
                cancellationToken);

        private Task<string?> ResolveTesseractAsync(CancellationToken cancellationToken) =>
            ResolveExecutableAsync(
                "TesseractPath",
                Array.Empty<string>(),
                new[] { "tesseract.exe", "tesseract" },
                "--version",
                cancellationToken);

        private string AbsolutePath(string relative)
        {
            if (!UploadPaths.TryResolveUploadPath(_env, relative, out var fullPath))
                throw new InvalidOperationException($"Invalid or disallowed path: {relative}");
            return fullPath;
        }

        private string SaveOutputPath(string fileName)
        {
            var safe = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "output.pdf" : fileName);
            return Path.Combine(_uploadsFolder, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{safe}");
        }

        private async Task<ProcessRunResult> RunProcessAsync(
            string fileName,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
                return new ProcessRunResult(-1, string.Empty, $"Failed to start {fileName}", false);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(timeout, cancellationToken);

            var completed = await Task.WhenAny(waitTask, timeoutTask);
            if (completed == timeoutTask)
            {
                TryKillProcess(process);
                var stdout = await SafeReadAsync(stdoutTask);
                var stderr = await SafeReadAsync(stderrTask);
                return new ProcessRunResult(-1, stdout, stderr, true);
            }

            await waitTask;
            return new ProcessRunResult(
                process.ExitCode,
                await SafeReadAsync(stdoutTask),
                await SafeReadAsync(stderrTask),
                false);
        }

        private static async Task<string> SafeReadAsync(Task<string> task)
        {
            try { return await task; }
            catch { return string.Empty; }
        }

        private static void TryKillProcess(System.Diagnostics.Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        public async Task<PdfOperationResult> OcrPdfAsync(string inputRelativePath, string outputTextFileRelative, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                var baseName = Path.GetFileNameWithoutExtension(abs);
                var tempRel = Path.Combine("uploads", $"{baseName}-ocr-temp").Replace('\\', '/');

                var convertResult = await ConvertPdfToJpgAsync(inputRelativePath, tempRel, cancellationToken);
                if (convertResult == null || !convertResult.Success) return new PdfOperationResult(false, convertResult?.Message ?? "Failed to convert PDF to images");

                var tempAbs = AbsolutePath(tempRel);
                if (!Directory.Exists(tempAbs)) return new PdfOperationResult(false, "Failed to generate images for OCR");

                // Find tesseract
                string? tessExe = await ResolveTesseractAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(tessExe))
                {
                    return new PdfOperationResult(false, "Tesseract OCR not found. Install Tesseract and ensure it is on PATH, or set Tools:TesseractPath / TESSERACT_PATH.");
                }

                var outputTextPath = outputTextFileRelative;
                if (string.IsNullOrWhiteSpace(outputTextPath))
                {
                    outputTextPath = Path.Combine("uploads", $"{baseName}-ocr.txt").Replace('\\', '/');
                }
                var outputAbsText = AbsolutePath(outputTextPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputAbsText) ?? _uploadsFolder);

                using var writer = new StreamWriter(outputAbsText, false);

                var images = Directory.EnumerateFiles(tempAbs, "page-*.jpg").OrderBy(n => n).ToList();
                if (!images.Any()) return new PdfOperationResult(false, "No images found for OCR");

                foreach (var img in images)
                {
                    var outBase = Path.Combine(Path.GetDirectoryName(img) ?? tempAbs, Path.GetFileNameWithoutExtension(img));
                    var run = await RunProcessAsync(tessExe, $"\"{img}\" \"{outBase}\" -l eng", TimeSpan.FromSeconds(45), cancellationToken);
                    if (run.TimedOut)
                    {
                        await writer.WriteLineAsync($"[OCR ERROR] Tesseract timed out for image {Path.GetFileName(img)}");
                        continue;
                    }

                    var txtFile = outBase + ".txt";
                    if (File.Exists(txtFile))
                    {
                        var t = await File.ReadAllTextAsync(txtFile, cancellationToken);
                        await writer.WriteLineAsync(t);
                    }
                    else
                    {
                        // If no text file, attempt to read stdout
                        if (!string.IsNullOrWhiteSpace(run.StandardOutput)) await writer.WriteLineAsync(run.StandardOutput);
                    }
                }

                var relative = outputTextPath.Replace('\\', '/');
                return new PdfOperationResult(true, "OCR completed", relative);
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> MergeAsync(IEnumerable<string> inputRelativePaths, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var output = new PdfDocument();
                var pagesAdded = 0;

                foreach (var rel in inputRelativePaths)
                {
                    var abs = AbsolutePath(rel);
                    if (!File.Exists(abs))
                        return new PdfOperationResult(false, $"Input file not found: {rel}");

                    using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < input.Pages.Count; i++)
                    {
                        output.AddPage(input.Pages[i]);
                        pagesAdded++;
                    }
                }

                if (pagesAdded == 0)
                    return new PdfOperationResult(false, "No pages to merge");

                var outPath = SaveOutputPath(outputFileName);
                output.Save(outPath);

                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Merged", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> SplitAsync(string inputRelativePath, IEnumerable<int> pagesOneBased, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();
                var pagesList = pagesOneBased.ToList();

                if (pagesList.Count == 0)
                    return new PdfOperationResult(false, "No pages specified");

                foreach (var p in pagesList)
                {
                    var idx = p - 1;
                    if (idx < 0 || idx >= input.Pages.Count)
                        return new PdfOperationResult(false, $"Page {p} is out of range (1-{input.Pages.Count})");

                    output.AddPage(input.Pages[idx]);
                }

                var outPath = SaveOutputPath(outputFileName);
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Split saved", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> ConvertJpgToPdfAsync(IEnumerable<string> inputImageRelativePaths, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var doc = new PdfDocument();

                foreach (var rel in inputImageRelativePaths)
                {
                    var abs = AbsolutePath(rel);
                    if (!File.Exists(abs)) continue;

                    using var imgStream = File.OpenRead(abs);
                    using var ximage = XImage.FromStream(() => imgStream);
                    var page = doc.AddPage();
                    // match page size to image
                    page.Width = XUnit.FromPoint(ximage.PixelWidth * 72.0 / ximage.HorizontalResolution);
                    page.Height = XUnit.FromPoint(ximage.PixelHeight * 72.0 / ximage.VerticalResolution);

                    using var gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawImage(ximage, 0, 0, page.Width, page.Height);
                }

                var outPath = SaveOutputPath(outputFileName);
                doc.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Converted images to PDF", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> ConvertPdfToJpgAsync(string inputRelativePath, string outputFolderRelative, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                // Determine output directory
                var outputRel = outputFolderRelative;
                if (string.IsNullOrWhiteSpace(outputRel))
                {
                    var baseName = Path.GetFileNameWithoutExtension(abs);
                    outputRel = Path.Combine("uploads", $"{baseName}-pages").Replace('\\', '/');
                }

                var outputDir = AbsolutePath(outputRel);
                Directory.CreateDirectory(outputDir);

                // Resolve Ghostscript executable: configured path (Tools:GhostscriptPath or GHOSTSCRIPT_PATH), then common install dirs / PATH
                string? gsExe = await ResolveGhostscriptAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(gsExe))
                {
                    return new PdfOperationResult(false, "Ghostscript not found on the server. Install Ghostscript and ensure 'gs' or 'gswin64c.exe' is on PATH, or set Tools:GhostscriptPath / GHOSTSCRIPT_PATH, or configure it at /admin/tools. See https://www.ghostscript.com/");
                }

                // Build Ghostscript arguments to render each page as JPEG
                // Example: gs -dNOPAUSE -dBATCH -sDEVICE=jpeg -r150 -sOutputFile=outdir/page-%03d.jpg input.pdf
                var outputPattern = Path.Combine(outputDir, "page-%03d.jpg");
                var args = $"-dNOPAUSE -dBATCH -sDEVICE=jpeg -r150 -dTextAlphaBits=4 -dGraphicsAlphaBits=4 -sOutputFile=\"{outputPattern}\" \"{abs}\"";

                var run = await RunProcessAsync(gsExe, args, TimeSpan.FromMinutes(2), cancellationToken);

                if (run.TimedOut)
                    return new PdfOperationResult(false, "Ghostscript PDF to JPG conversion timed out after 2 minutes.");

                if (run.ExitCode != 0)
                {
                    return new PdfOperationResult(false, $"Ghostscript failed: {run.ExitCode}. {run.StandardError}");
                }

                // Enumerate produced files
                var produced = Directory.EnumerateFiles(outputDir, "page-*.jpg").OrderBy(n => n).ToList();
                if (!produced.Any()) return new PdfOperationResult(false, "Ghostscript completed but no output files found.");

                // Return the relative folder path for UI consumption
                var relative = outputRel.Replace('\\', '/');
                return new PdfOperationResult(true, "Rendered PDF to JPG images", relative);
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> AddWatermarkAsync(string inputRelativePath, string watermarkText, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();

                for (int i = 0; i < input.Pages.Count; i++)
                {
                    var page = output.AddPage(input.Pages[i]);
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
                    var font = new XFont("Arial", 40, XFontStyle.Bold);
                    var color = XColor.FromArgb(80, 0, 0, 0);
                    var brush = new XSolidBrush(color);
                    var size = gfx.MeasureString(watermarkText, font);
                    var state = gfx.Save();
                    gfx.TranslateTransform(page.Width / 2, page.Height / 2);
                    gfx.RotateTransform(-45);
                    gfx.DrawString(watermarkText, font, brush, -size.Width / 2, 0);
                    gfx.Restore(state);
                }

                var outPath = SaveOutputPath(outputFileName);
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Watermark added", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> AddPageNumbersAsync(string inputRelativePath, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();

                for (int i = 0; i < input.Pages.Count; i++)
                {
                    var page = output.AddPage(input.Pages[i]);
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
                    var font = new XFont("Arial", 10, XFontStyle.Regular);
                    var text = $"Page {i + 1} of {input.Pages.Count}";
                    var size = gfx.MeasureString(text, font);
                    gfx.DrawString(text, font, XBrushes.Black, (page.Width - size.Width) / 2, page.Height - 20);
                }

                var outPath = SaveOutputPath(outputFileName);
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Page numbers added", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> CompressPdfAsync(string inputRelativePath, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                string? gsExe = await ResolveGhostscriptAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(gsExe))
                {
                    return new PdfOperationResult(false, "Ghostscript not found on the server. Install Ghostscript and ensure 'gs' or 'gswin64c.exe' is on PATH, or set Tools:GhostscriptPath / GHOSTSCRIPT_PATH, or configure it at /admin/tools.");
                }

                var outPath = SaveOutputPath(outputFileName ?? (Path.GetFileNameWithoutExtension(abs) + "-compressed.pdf"));
                var args = $"-dNOPAUSE -dBATCH -sDEVICE=pdfwrite -dCompatibilityLevel=1.4 -dPDFSETTINGS=/ebook -sOutputFile=\"{outPath}\" \"{abs}\"";

                var run = await RunProcessAsync(gsExe, args, TimeSpan.FromMinutes(2), cancellationToken);

                if (run.TimedOut)
                    return new PdfOperationResult(false, "Ghostscript compression timed out after 2 minutes.");

                if (run.ExitCode != 0)
                    return new PdfOperationResult(false, $"Ghostscript compression failed: {run.StandardError}");

                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return new PdfOperationResult(true, "Compression complete", relative);
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> ConvertPdfToWordAsync(string inputRelativePath, string outputFileName, CancellationToken cancellationToken = default)
        {
            return await ConvertViaLibreOfficeAsync(inputRelativePath, "docx", outputFileName, cancellationToken);
        }

        public async Task<PdfOperationResult> ConvertViaLibreOfficeAsync(
            string inputRelativePath, 
            string targetExtension, 
            string outputFileName, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                string? soffice = await ResolveLibreOfficeAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(soffice))
                {
                    return new PdfOperationResult(false, "LibreOffice (soffice) not found on the server. Install LibreOffice and ensure 'soffice' is on PATH, or set Tools:LibreOfficePath / LIBREOFFICE_PATH, or configure it at /admin/tools.");
                }

                var tempDirName = $"{Path.GetFileNameWithoutExtension(abs)}-conv-{Guid.NewGuid():N}";
                var outDirRel = Path.Combine("uploads", tempDirName).Replace('\\', '/');
                var outDirAbs = AbsolutePath(outDirRel);
                Directory.CreateDirectory(outDirAbs);

                // Run LibreOffice conversion
                // Headless LibreOffice is highly versatile: converts PPT/Excel/Word/HTML/RTF/ODT to PDF, or PDF to Word/PPT/Excel.
                var run = await RunProcessAsync(
                    soffice,
                    $"--headless --nologo --nofirststartwizard --convert-to {targetExtension} --outdir \"{outDirAbs}\" \"{abs}\"",
                    TimeSpan.FromMinutes(2),
                    cancellationToken);

                if (run.TimedOut)
                    return new PdfOperationResult(false, "LibreOffice conversion timed out after 2 minutes.");

                if (run.ExitCode != 0)
                    return new PdfOperationResult(false, $"LibreOffice conversion failed: {run.StandardError}{run.StandardOutput}");

                // Find generated file
                var pattern = $"*.{targetExtension}";
                var generated = Directory.EnumerateFiles(outDirAbs, pattern).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (generated == null)
                    return new PdfOperationResult(false, $"LibreOffice completed but no {targetExtension.ToUpperInvariant()} output was found. {run.StandardOutput}{run.StandardError}");

                var safeOutputName = string.IsNullOrWhiteSpace(outputFileName)
                    ? $"{Path.GetFileNameWithoutExtension(abs)}.{targetExtension}"
                    : Path.ChangeExtension(Path.GetFileName(outputFileName), $".{targetExtension}");

                var finalPath = SaveOutputPath(safeOutputName);
                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(generated, finalPath);
                TryDeleteDirectory(outDirAbs);

                var rel = Path.Combine("uploads", Path.GetFileName(finalPath)).Replace('\\', '/');
                return new PdfOperationResult(true, $"Converted successfully", rel);
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> DeletePdfPagesAsync(
            string inputRelativePath, 
            IEnumerable<int> pagesToDelete, 
            string outputFileName, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();
                var toDelete = pagesToDelete.ToHashSet();

                var pagesAdded = 0;
                for (int i = 0; i < input.Pages.Count; i++)
                {
                    if (toDelete.Contains(i + 1)) continue; // 1-based index
                    output.AddPage(input.Pages[i]);
                    pagesAdded++;
                }

                if (pagesAdded == 0)
                    return new PdfOperationResult(false, "Cannot delete all pages. A PDF must contain at least one page.");

                var outPath = SaveOutputPath(outputFileName ?? "pages-deleted.pdf");
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Pages deleted successfully", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> RotatePdfAsync(
            string inputRelativePath, 
            int angle, 
            IEnumerable<int> pagesToRotate, 
            string outputFileName, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();
                var toRotate = pagesToRotate.ToHashSet();

                for (int i = 0; i < input.Pages.Count; i++)
                {
                    var page = output.AddPage(input.Pages[i]);
                    if (toRotate.Count == 0 || toRotate.Contains(i + 1))
                    {
                        page.Rotate = (page.Rotate + angle) % 360;
                    }
                }

                var outPath = SaveOutputPath(outputFileName ?? "rotated.pdf");
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "Pages rotated successfully", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> ProtectPdfAsync(
            string inputRelativePath, 
            string password, 
            string outputFileName, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();

                for (int i = 0; i < input.Pages.Count; i++)
                {
                    output.AddPage(input.Pages[i]);
                }

                output.SecuritySettings.UserPassword = password;
                output.SecuritySettings.OwnerPassword = password;
                output.SecuritySettings.DocumentSecurityLevel = PdfSharpCore.Pdf.Security.PdfDocumentSecurityLevel.Encrypted128Bit;
                output.SecuritySettings.PermitPrint = true;
                output.SecuritySettings.PermitAccessibilityExtractContent = true;

                var outPath = SaveOutputPath(outputFileName ?? "protected.pdf");
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "PDF password-protected successfully", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }

        public async Task<PdfOperationResult> UnlockPdfAsync(
            string inputRelativePath, 
            string password, 
            string outputFileName, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                using var input = PdfReader.Open(abs, password, PdfDocumentOpenMode.Import);
                var output = new PdfDocument();

                for (int i = 0; i < input.Pages.Count; i++)
                {
                    output.AddPage(input.Pages[i]);
                }

                var outPath = SaveOutputPath(outputFileName ?? "unlocked.pdf");
                output.Save(outPath);
                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return await Task.FromResult(new PdfOperationResult(true, "PDF unlocked successfully", relative));
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, $"Failed to unlock PDF: {ex.Message}");
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch { }
        }
    }
}
