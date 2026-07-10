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
    public class PdfProcessor : IPdfProcessor
    {
        private readonly string _uploadsFolder;
        private readonly IWebHostEnvironment _env;

        public PdfProcessor(IWebHostEnvironment env)
        {
            _env = env;
            _uploadsFolder = UploadPaths.GetUploadsRoot(env);
            if (!Directory.Exists(_uploadsFolder)) Directory.CreateDirectory(_uploadsFolder);
        }

        private string AbsolutePath(string relative)
        {
            if (!UploadPaths.TryResolveUploadPath(_env, relative, out var fullPath))
                throw new InvalidOperationException($"Invalid or disallowed path: {relative}");
            return fullPath;
        }

        private string SaveOutputPath(string fileName)
        {
            var safe = Path.GetFileName(fileName);
            return Path.Combine(_uploadsFolder, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{safe}");
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
                string? tessExe = null;
                foreach (var t in tessCandidates)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = t,
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p != null)
                        {
                            await p.WaitForExitAsync(cancellationToken);
                            if (p.ExitCode == 0)
                            {
                                tessExe = t;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore and try next candidate
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
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = tessExe,
                        Arguments = $"\"{img}\" \"{outBase}\" -l eng",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p == null)
                    {
                        await writer.WriteLineAsync($"[OCR ERROR] Failed to start tesseract for image {Path.GetFileName(img)}");
                        continue;
                    }

                    var so = await p.StandardOutput.ReadToEndAsync(cancellationToken);
                    var se = await p.StandardError.ReadToEndAsync(cancellationToken);
                    await p.WaitForExitAsync(cancellationToken);

                    var txtFile = outBase + ".txt";
                    if (File.Exists(txtFile))
                    {
                        var t = await File.ReadAllTextAsync(txtFile, cancellationToken);
                        await writer.WriteLineAsync(t);
                    }
                    else
                    {
                        // If no text file, attempt to read stdout
                        if (!string.IsNullOrWhiteSpace(so)) await writer.WriteLineAsync(so);
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

                // Try to find Ghostscript executable
                string[] candidates = { "gs", "gswin64c.exe", "gswin32c.exe" };
                string? gsExe = null;
                foreach (var c in candidates)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = c,
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p != null)
                        {
                            await p.WaitForExitAsync(cancellationToken);
                            if (p.ExitCode == 0)
                            {
                                gsExe = c;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore and try next candidate
                    }
                }

                if (gsExe == null)
                {
                    return new PdfOperationResult(false, "Ghostscript not found on the server. Install Ghostscript and ensure 'gs' or 'gswin64c.exe' is on PATH. See https://www.ghostscript.com/");
                }

                // Build Ghostscript arguments to render each page as JPEG
                // Example: gs -dNOPAUSE -dBATCH -sDEVICE=jpeg -r150 -sOutputFile=outdir/page-%03d.jpg input.pdf
                var outputPattern = Path.Combine(outputDir, "page-%03d.jpg");
                var args = $"-dNOPAUSE -dBATCH -sDEVICE=jpeg -r150 -dTextAlphaBits=4 -dGraphicsAlphaBits=4 -sOutputFile=\"{outputPattern}\" \"{abs}\"";

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = gsExe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(startInfo);
                if (proc == null) return new PdfOperationResult(false, "Failed to start Ghostscript process");

                var stdOut = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErr = await proc.StandardError.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken);

                if (proc.ExitCode != 0)
                {
                    return new PdfOperationResult(false, $"Ghostscript failed: {proc.ExitCode}. {stdErr}");
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

        public Task<PdfOperationResult> CompressPdfAsync(string inputRelativePath, string outputFileName, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return Task.FromResult(new PdfOperationResult(false, "Input file not found"));

                // Find Ghostscript
                string[] candidates = { "gs", "gswin64c.exe", "gswin32c.exe" };
                string? gsExe = null;
                foreach (var c in candidates)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = c,
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p != null)
                        {
                            p.WaitForExit();
                            if (p.ExitCode == 0)
                            {
                                gsExe = c;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (gsExe == null)
                {
                    return Task.FromResult(new PdfOperationResult(false, "Ghostscript not found on the server. Install Ghostscript and ensure 'gs' or 'gswin64c.exe' is on PATH."));
                }

                var outPath = SaveOutputPath(outputFileName ?? (Path.GetFileNameWithoutExtension(abs) + "-compressed.pdf"));
                var args = $"-dNOPAUSE -dBATCH -sDEVICE=pdfwrite -dCompatibilityLevel=1.4 -dPDFSETTINGS=/ebook -sOutputFile=\"{outPath}\" \"{abs}\"";

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = gsExe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(startInfo);
                if (proc == null) return Task.FromResult(new PdfOperationResult(false, "Failed to start Ghostscript process"));
                var stdOut = proc.StandardOutput.ReadToEnd();
                var stdErr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0) return Task.FromResult(new PdfOperationResult(false, $"Ghostscript compression failed: {stdErr}"));

                var relative = Path.Combine("uploads", Path.GetFileName(outPath)).Replace('\\', '/');
                return Task.FromResult(new PdfOperationResult(true, "Compression complete", relative));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new PdfOperationResult(false, ex.Message));
            }
        }

        public async Task<PdfOperationResult> ConvertPdfToWordAsync(string inputRelativePath, string outputRelativePath, CancellationToken cancellationToken = default)
        {
            try
            {
                var abs = AbsolutePath(inputRelativePath);
                if (!File.Exists(abs)) return new PdfOperationResult(false, "Input file not found");

                // Find LibreOffice / soffice
                string[] candidates = { "soffice", "libreoffice", "soffice.exe" };
                string? soffice = null;
                foreach (var c in candidates)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = c,
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p != null)
                        {
                            await p.WaitForExitAsync(cancellationToken);
                            if (p.ExitCode == 0)
                            {
                                soffice = c;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (soffice == null)
                {
                    return new PdfOperationResult(false, "LibreOffice (soffice) not found. Install LibreOffice and ensure 'soffice' is on PATH.");
                }

                var outDirRel = outputRelativePath;
                if (string.IsNullOrWhiteSpace(outDirRel))
                {
                    outDirRel = Path.Combine("uploads", "office-out").Replace('\\', '/');
                }
                var outDirAbs = AbsolutePath(outDirRel);
                Directory.CreateDirectory(outDirAbs);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = soffice,
                    Arguments = $"--headless --convert-to docx --outdir \"{outDirAbs}\" \"{abs}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(startInfo);
                if (proc == null) return new PdfOperationResult(false, "Failed to start LibreOffice process");
                var so = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                var se = await proc.StandardError.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken);

                if (proc.ExitCode != 0) return new PdfOperationResult(false, $"LibreOffice conversion failed: {se}");

                // Find generated file
                var generated = Directory.EnumerateFiles(outDirAbs).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(abs), StringComparison.OrdinalIgnoreCase) && Path.GetExtension(f).Equals(".docx", StringComparison.OrdinalIgnoreCase));
                if (generated == null) return new PdfOperationResult(false, "LibreOffice completed but no output found");

                var rel = Path.Combine(outDirRel, Path.GetFileName(generated)).Replace('\\', '/');
                return new PdfOperationResult(true, "Converted to Word (docx)", rel);
            }
            catch (Exception ex)
            {
                return new PdfOperationResult(false, ex.Message);
            }
        }
    }
}
