using System;

namespace Bigpdf.Models
{
    public enum JobStatus { Pending, Running, Completed, Failed }

    public enum JobType
    {
        PdfToJpg,
        CompressPdf,
        OcrPdf,
        PdfToWord,
        Merge,
        Split,
        AddPageNumbers,
        PptToPdf,
        PdfToPpt,
        ExcelToPdf,
        PdfToExcel,
        WordToPdf,
        TxtToPdf,
        RtfToPdf,
        OdtToPdf,
        HtmlToPdf,
        DeletePages,
        RotatePdf,
        ProtectPdf,
        UnlockPdf
    }

    public class JobInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public JobType Type { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public string? Message { get; set; }
        public string? ResultPath { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? ProgressPercent { get; set; }
    }
}
