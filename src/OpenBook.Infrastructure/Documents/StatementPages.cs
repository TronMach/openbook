using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Common;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OpenBook.Infrastructure.Documents;

/// <summary>
/// The pages of a statement: told apart, split out of the file, and read on this server (the words a PDF holds exactly, or
/// the picture of a page through the Tesseract program, enlarged first because the small print of a scan is read far
/// better twice its size). Nothing leaves the server here.
/// </summary>
public sealed class StatementPages(string? tesseractPath) : IStatementPages
{
    /// <summary>Pictures bigger than this aren't enlarged, or looked at for a preview.</summary>
    private const long MaxPixels = 40_000_000;

    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(90);

    public bool CanReadPictures => tesseractPath is not null && File.Exists(tesseractPath);

    public IReadOnlyList<StatementPage> Inspect(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsPdf(fileName))
            return [new StatementPage(1, null, null)];
        return Opened(content, document => (IReadOnlyList<StatementPage>)[.. document.GetPages().Take(SettlementCaptureService.MaxPages + 1).Select(p =>
        {
            var text = string.Join(' ', p.GetWords().Take(14).Select(w => w.Text));
            return new StatementPage(p.Number, text.Length > 0 ? TextCleaning.Clean(text) : null, text.Length == 0 && Picture(p) is { } picture ? picture.Shrunk(480).ToPng() : null);
        })]);
    }

    public byte[] Extract(string fileName, byte[] content, IReadOnlyCollection<int> pages)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(pages);
        if (!IsPdf(fileName))
            return content;
        try
        {
            using var input = new MemoryStream(content);
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            using var chosen = new PdfSharp.Pdf.PdfDocument();
            foreach (var page in pages)
                chosen.AddPage(source.Pages[page - 1]);
            using var output = new MemoryStream();
            chosen.Save(output, closeStream: false);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Never the whole file instead: the pages left out may be somebody else's pay.
            throw new DomainException("settlement.cannot_split", "The pages can't be taken out of this PDF, so none of it is read. Print the pages you want to a new PDF and use that.");
        }
    }

    public async Task<StatementWords?> WordsAsync(string fileName, byte[] content, IReadOnlyCollection<int> pages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(pages);
        if (!IsPdf(fileName))
        {
            return CanReadPictures && await OcrAsync(content, Path.GetExtension(fileName), 1, ct) is { } read ? new StatementWords(true, read) : null;
        }

        var (exact, pictures) = Opened(content, document =>
        {
            var words = new List<StatementWord>();
            var images = new List<(int Page, byte[] Png)>();
            foreach (var page in document.GetPages().Where(p => pages.Contains(p.Number)))
            {
                var own = page.GetWords().Select(w => new StatementWord(page.Number, TextCleaning.Clean(w.Text), 100,
                    w.BoundingBox.Left, page.Height - w.BoundingBox.Top, w.BoundingBox.Width, Math.Max(w.BoundingBox.Height, 1))).ToList();
                words.AddRange(own);
                if (own.Count == 0 && Picture(page) is { } picture)
                    images.Add((page.Number, picture.Enlarged().ToPng()));
            }

            return (words, images);
        });
        if (pictures.Count == 0)
            return exact.Count > 0 ? new StatementWords(false, exact) : null;
        if (!CanReadPictures)
            return null;
        foreach (var (page, png) in pictures)
        {
            if (await OcrAsync(png, ".png", page, ct) is { } read)
                exact.AddRange(read);
        }

        return new StatementWords(true, exact);
    }

    private static bool IsPdf(string fileName) => Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static T Opened<T>(byte[] content, Func<PdfDocument, T> read)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            return read(document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not DomainException)
        {
            throw new DomainException("settlement.file_unreadable", "That PDF can't be opened: it may be damaged or locked with a password.");
        }
    }

    /// <summary>The picture a scanned page is made of: its biggest image, as shades of gray.</summary>
    private static Gray? Picture(Page page) =>
        page.GetImages().OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples).Select(Gray.From).FirstOrDefault(g => g is not null);

    private async Task<IReadOnlyList<StatementWord>?> OcrAsync(byte[] picture, string extension, int page, CancellationToken ct)
    {
        var input = Path.Combine(Path.GetTempPath(), $"openbook-statement-{Guid.NewGuid():N}{extension}");
        try
        {
            await File.WriteAllBytesAsync(input, picture, ct);
            var start = new ProcessStartInfo(tesseractPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var argument in new[] { input, "stdout", "--psm", "6", "tsv" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Tesseract didn't start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(OcrTimeout);
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await errors;
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 ? ReadTsv(output, page) : null;
            }
            catch (OperationCanceledException)
            {
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

    /// <summary>Tesseract's table of words: level, page, block, paragraph, line, word, left, top, width, height, confidence, text.</summary>
    public static IReadOnlyList<StatementWord> ReadTsv(string tsv, int page)
    {
        ArgumentNullException.ThrowIfNull(tsv);
        var words = new List<StatementWord>();
        foreach (var line in tsv.Split('\n'))
        {
            var cells = line.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5")
                continue;
            double Cell(int i) => double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            var text = TextCleaning.Clean(cells[11]).Trim();
            if (text.Length > 0 && Cell(10) >= 0)
                words.Add(new StatementWord(page, text, Cell(10), Cell(6), Cell(7), Cell(8), Cell(9)));
        }

        return words;
    }

    /// <summary>A picture in shades of gray, 0 black to 255 white, dark writing on a light page.</summary>
    internal sealed class Gray(int width, int height, byte[] shades)
    {
        public int Width => width;
        public int Height => height;

        public static Gray? From(IPdfImage image)
        {
            var (w, h, bits) = (image.WidthInSamples, image.HeightInSamples, image.BitsPerComponent);
            if (w <= 0 || h <= 0 || (long)w * h > MaxPixels || !image.TryGetBytesAsMemory(out var memory))
                return null;
            var bytes = memory.Span;
            var shades = new byte[w * h];
            if (bits == 1 && bytes.Length >= (w + 7) / 8 * h)
            {
                var stride = (w + 7) / 8;
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                        shades[y * w + x] = (bytes[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;
                }
            }
            else if (bits == 8 && bytes.Length >= (long)w * h * 3)
            {
                for (var i = 0; i < shades.Length; i++)
                    shades[i] = (byte)((bytes[i * 3] * 299 + bytes[i * 3 + 1] * 587 + bytes[i * 3 + 2] * 114) / 1000);
            }
            else if (bits == 8 && bytes.Length >= w * h)
            {
                bytes[..shades.Length].CopyTo(shades);
            }
            else
            {
                return null;
            }

            // Masks and some scans keep the writing as the light part: a page is mostly paper.
            if (shades.Count(s => s < 128) > shades.Length / 2)
            {
                for (var i = 0; i < shades.Length; i++)
                    shades[i] = (byte)(255 - shades[i]);
            }

            return new Gray(w, h, shades);
        }

        /// <summary>Twice the size and softened, which the OCR program reads much better than small sharp print.</summary>
        public Gray Enlarged()
        {
            if ((long)width * height * 4 > MaxPixels * 2)
                return this;
            var (w, h) = (width * 2, height * 2);
            var big = new byte[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    // Between the four nearest, by how near each is.
                    var (sx, sy) = (Math.Max(0, 2 * x - 1), Math.Max(0, 2 * y - 1));
                    var (x0, y0) = (Math.Min(sx / 4, width - 1), Math.Min(sy / 4, height - 1));
                    var (x1, y1) = (Math.Min(x0 + 1, width - 1), Math.Min(y0 + 1, height - 1));
                    var (fx, fy) = (sx % 4, sy % 4);
                    var top = shades[y0 * width + x0] * (4 - fx) + shades[y0 * width + x1] * fx;
                    var bottom = shades[y1 * width + x0] * (4 - fx) + shades[y1 * width + x1] * fx;
                    big[y * w + x] = (byte)((top * (4 - fy) + bottom * fy) / 16);
                }
            }

            return new Gray(w, h, Softened(Softened(big, w, h, horizontal: true), w, h, horizontal: false));
        }

        // A short bell-shaped blur one way: 1 4 6 4 1.
        private static byte[] Softened(byte[] from, int w, int h, bool horizontal)
        {
            var to = new byte[from.Length];
            var step = horizontal ? 1 : w;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var at = y * w + x;
                    var (place, last) = horizontal ? (x, w - 1) : (y, h - 1);
                    int Shade(int offset) => from[at + (Math.Clamp(place + offset, 0, last) - place) * step];
                    to[at] = (byte)((Shade(-2) + 4 * Shade(-1) + 6 * Shade(0) + 4 * Shade(1) + Shade(2)) / 16);
                }
            }

            return to;
        }

        /// <summary>No wider than this, each dot the average of those it stands for.</summary>
        public Gray Shrunk(int maxWidth)
        {
            var by = (width + maxWidth - 1) / maxWidth;
            if (by <= 1)
                return this;
            var (w, h) = (Math.Max(1, width / by), Math.Max(1, height / by));
            var small = new byte[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < by; dy++)
                    {
                        for (var dx = 0; dx < by; dx++)
                            sum += shades[(y * by + dy) * width + x * by + dx];
                    }

                    small[y * w + x] = (byte)(sum / (by * by));
                }
            }

            return new Gray(w, h, small);
        }

        public byte[] ToPng()
        {
            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            Chunk(png, "IHDR"u8, header);
            using var data = new MemoryStream();
            using (var zip = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    zip.WriteByte(0);
                    zip.Write(shades, y * width, width);
                }
            }

            Chunk(png, "IDAT"u8, data.ToArray());
            Chunk(png, "IEND"u8, []);
            return png.ToArray();
        }

        private static void Chunk(MemoryStream png, ReadOnlySpan<byte> type, byte[] data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            png.Write(number);
            png.Write(type);
            png.Write(data);
            var crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
            BinaryPrimitives.WriteUInt32BigEndian(number, crc);
            png.Write(number);
        }

        private static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }

            return crc;
        }
    }
}
