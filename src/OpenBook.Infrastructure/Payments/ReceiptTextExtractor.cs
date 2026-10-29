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

    public bool CanReadImages => tesseractPath is not null && File.Exists(tesseractPath);

    public async Task<string?> ExtractAsync(string fileName, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return Pdf(content);
        if (extension.ToLowerInvariant() is ".txt" or ".csv" or ".eml")
            return Encoding.UTF8.GetString(content);
        if (Images.Contains(extension) && CanReadImages)
            return await OcrAsync(extension, content, ct);
        return null;
    }

    private static string? Pdf(byte[] content)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var text = new StringBuilder();
            foreach (var page in document.GetPages().Take(5))
            {
                // Lines as they sit on the page: words grouped by their baseline, left to right.
                foreach (var line in page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom)).OrderByDescending(g => g.Key))
                    text.AppendLine(string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            }

            return text.Length == 0 ? null : text.ToString();
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
            var start = new ProcessStartInfo(tesseractPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(input);
            start.ArgumentList.Add("stdout");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Tesseract didn't start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 && output.Trim().Length > 0 ? output : null;
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
