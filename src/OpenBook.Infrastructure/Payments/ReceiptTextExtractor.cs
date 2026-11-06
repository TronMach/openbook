using System.Diagnostics;
using System.Text;
using OpenBook.Application.Purchases;
using UglyToad.PdfPig;

namespace OpenBook.Infrastructure.Payments;

/// <summary>
/// Reads receipts locally: text from PDFs (PdfPig; scanned PDFs have none) and text files, and images through the
/// Tesseract OCR command when <c>OpenBook:Ocr:TesseractPath</c> points at it. Nothing leaves the server.
/// </summary>
public sealed class ReceiptTextExtractor(string? tesseractPath) : IReceiptTextExtractor
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".tif", ".tiff", ".bmp" };

    /// <summary>The pages read of a PDF: enough for any invoice, and a bound on the work one upload can make.</summary>
    public const int MaxPages = 50;

    /// <summary>The end of what's read from a longer PDF.</summary>
    public const string PagesLeftOut = OpenBook.Application.Documents.DocumentTextNotes.PagesLeftOut;

    public bool CanReadImages => tesseractPath is not null && File.Exists(tesseractPath);

    public async Task<string?> ExtractAsync(string fileName, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return Pdf(content);
        if (extension.ToLowerInvariant() is ".txt" or ".csv" or ".eml")
            return Readable(OpenBook.Application.Import.TextFiles.Decode(content));
        if (Images.Contains(extension) && CanReadImages)
            return await OcrAsync(extension, content, ct);
        return null;
    }

    /// <summary>Without what a database can't hold, or a person read: control characters and half a character.</summary>
    private static string Readable(string text) => Domain.Common.TextCleaning.Clean(text);

    private static string? Pdf(byte[] content)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var text = new StringBuilder();
            foreach (var page in document.GetPages().Take(MaxPages))
            {
                // Lines as they sit on the page: words grouped by their baseline, left to right.
                foreach (var line in page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom)).OrderByDescending(g => g.Key))
                    text.AppendLine(string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            }

            if (document.NumberOfPages > MaxPages)
                text.Append(PagesLeftOut).Append(FormattableString.Invariant($" {MaxPages} of {document.NumberOfPages}]")).AppendLine();
            return text.Length == 0 ? null : Readable(text.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Damaged or encrypted PDFs: kept as a file, with nothing read.
            return null;
        }
    }

    private async Task<string?> OcrAsync(string extension, byte[] content, CancellationToken ct)
    {
        var input = Path.Combine(Path.GetTempPath(), $"openbook-receipt-{Guid.NewGuid():N}{extension}");
        try
        {
            await File.WriteAllBytesAsync(input, content, ct);
            var start = new ProcessStartInfo(tesseractPath!) { RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(input);
            start.ArgumentList.Add("stdout");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Tesseract didn't start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 && output.Trim().Length > 0 ? output : null;
            }
            catch (OperationCanceledException)
            {
                // Stopped, not abandoned: it would go on using a core, and hold the file.
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
        finally
        {
            File.Delete(input);
        }
    }
}
