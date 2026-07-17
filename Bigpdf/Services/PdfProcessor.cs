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
            // 1) Environment variable
            try
            {
                var fromEnv = Environment.GetEnvironmentVariable(key.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
            }
            catch { }

            // 2) appsettings (Tools:Key)
            try
            {
                var cfg = _configuration?.GetSection("Tools")?[key];
                if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
            }
            catch { }

            // 3) local toolpaths.json in content root
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
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch { }

            return null;
        }

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
                string[] tessCandidates = { "tesseract", "tesseract.exe" };
                string? tessExe = GetConfiguredToolPath("TesseractPath");
                if (string.IsNullOrWhiteSpace(tessExe))
                {
                    foreach (var t in tessCandidates)
                    {
                        try
                        {
                            var probe = await RunProcessAsync(t, "--version", TimeSpan.FromSeconds(10), cancellationToken);
                            if (probe.ExitCode == 0)
                            {
                                tessExe = t;
                                break;
                            }
                        }
                        catch
                        {
                            // ignore and try next candidate
                        }
                    }
                }

                if (tessExe == null)
                {
                    return new PdfOperationResult(false, "Tesseract not found on the server. Install Tesseract OCR and ensure 'tesseract' is on PATH. See https://github.com/tesseract-ocr/tesseract");
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

                // Resolve Ghostscript executable: configured path (Tools:GhostscriptPath or GHOSTSCRIPT_PATH), then PATH candidates
                string? gsExe = GetConfiguredToolPath("GhostscriptPath");
                if (string.IsNullOrWhiteSpace(gsExe))
                {
                    string[] candidates = { "gs", "gswin64c.exe", "gswin32c.exe" };
                    foreach (var c in candidates)
                    {
                        try
                        {
                            var probe = await RunProcessAsync(c, "--version", TimeSpan.FromSeconds(10), cancellationToken);
                            if (probe.ExitCode == 0)
                            {
                                gsExe = c;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (string.IsNullOrWhiteSpace(gsExe))
                {
                    return new PdfOperationResult(false, "Ghostscript not found on the server. Install Ghostscript and ensure 'gs' or 'gswin64c.exe' is on PATH, or set the Tools:GhostscriptPath configuration or GHOSTSCRIPT_PATH environment variable to the gs executable path. See https://www.ghostscript.com/");
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

                string? gsExe = GetConfiguredToolPath("GhostscriptPath");
                if (string.IsNullOrWhiteSpace(gsExe))
                {
                    string[] candidates = { "gs", "gswin64c.exe", "gswin32c.exe" };
                    foreach (var c in candidates)
                    {
                        try
                        {
                            var probe = await RunProcessAsync(c, "--version", TimeSpan.FromSeconds(10), cancellationToken);
                            if (probe.ExitCode == 0)
                            {
                                gsExe = c;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (gsExe == null)
                {
                    return new PdfOperationResult(false, "Ghostscript not found on the server. Install Ghostscript and ensure 'gs' or 'gswin64c.exe' is on PATH, or set Tools:GhostscriptPath.");
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
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                var safeOutputName = string.IsNullOrWhiteSpace(outputFileName)
                    ? $"{Path.GetFileNameWithoutExtension(abs)}.docx"
                    : Path.ChangeExtension(Path.GetFileName(outputFileName), ".docx");

                // Resolve soffice path: configured path (Tools:LibreOfficePath or LIBREOFFICE_PATH), then PATH candidates
                string? soffice = GetConfiguredToolPath("LibreOfficePath");
                if (string.IsNullOrWhiteSpace(soffice))
                {
                    string[] candidates = { "soffice", "libreoffice", "soffice.exe" };
                    foreach (var c in candidates)
                    {
                        try
                        {
                            var probe = await RunProcessAsync(c, "--version", TimeSpan.FromSeconds(10), cancellationToken);
                            if (probe.ExitCode == 0)
                            {
                                soffice = c;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (string.IsNullOrWhiteSpace(soffice))
                {
                    return new PdfOperationResult(false, "LibreOffice (soffice) not found. Install LibreOffice and ensure 'soffice' is on PATH, or set the Tools:LibreOfficePath configuration or LIBREOFFICE_PATH environment variable to the soffice executable path.");
                }

                var tempDirName = $"{Path.GetFileNameWithoutExtension(abs)}-word-{Guid.NewGuid():N}";
                var outDirRel = Path.Combine("uploads", tempDirName).Replace('\\', '/');
                var outDirAbs = AbsolutePath(outDirRel);
                Directory.CreateDirectory(outDirAbs);

                var run = await RunProcessAsync(
                    soffice,
                    $"--headless --nologo --nofirststartwizard --convert-to docx --outdir \"{outDirAbs}\" \"{abs}\"",
                    TimeSpan.FromMinutes(2),
                    cancellationToken);

                if (run.TimedOut)
                    return new PdfOperationResult(false, "LibreOffice PDF to Word conversion timed out after 2 minutes. Try a smaller PDF or check that LibreOffice is installed correctly.");

                if (run.ExitCode != 0)
                    return new PdfOperationResult(false, $"LibreOffice conversion failed: {run.StandardError}{run.StandardOutput}");

                // Find generated file
                var generated = Directory.EnumerateFiles(outDirAbs, "*.docx").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (generated == null)
                    return new PdfOperationResult(false, $"LibreOffice completed but no DOCX output was found. {run.StandardOutput}{run.StandardError}");

                var finalPath = SaveOutputPath(safeOutputName);
                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(generated, finalPath);
                TryDeleteDirectory(outDirAbs);

                var rel = Path.Combine("uploads", Path.GetFileName(finalPath)).Replace('\\', '/');
                return new PdfOperationResult(true, "Converted to Word (DOCX)", rel);
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
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
