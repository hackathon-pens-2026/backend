using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SignIt.Infrastructure.Errors;

namespace SignIt.Modules.Signatures.Services;

public sealed class PdfSharpOverlayService : IPdfOverlayService
{
    private static readonly TimeZoneInfo WibTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Jakarta");

    public PdfSharpOverlayService()
    {
        SignItFontResolver.EnsureRegistered();
    }

    public Task<byte[]> OverlaySignaturesAsync(
        byte[] sourcePdfBytes,
        IReadOnlyList<PdfSignatureOverlayItem> items,
        string? documentVerificationCode = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (sourcePdfBytes == null || sourcePdfBytes.Length < 4)
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_pdf", "File PDF tidak valid atau kosong.");

        // Check magic bytes %PDF
        if (sourcePdfBytes[0] != 0x25 || sourcePdfBytes[1] != 0x50 || sourcePdfBytes[2] != 0x44 || sourcePdfBytes[3] != 0x46)
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_pdf_header", "Header file bukan format PDF yang sah.");

        try
        {
            using var inputStream = new MemoryStream(sourcePdfBytes);
            using var document = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

            if (document.PageCount == 0)
                throw new SignItDomainException(DomainErrorKind.Validation, "empty_pdf_pages", "PDF tidak memiliki halaman.");

            XFont? fontRegular = null;
            XFont? fontBold = null;
            XFont? fontSmall = null;
            try
            {
                fontRegular = new XFont("Arial", 8, XFontStyleEx.Regular);
                fontBold = new XFont("Arial", 8, XFontStyleEx.Bold);
                fontSmall = new XFont("Arial", 7, XFontStyleEx.Regular);
            }
            catch (Exception ex)
            {
                throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "signature_fonts_unavailable", ex.Message);
            }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.PageIndex < 0 || item.PageIndex >= document.PageCount)
                    throw new SignItDomainException(DomainErrorKind.Validation, "signature_slot_invalid", "Halaman slot tanda tangan tidak tersedia.");

                var page = document.Pages[item.PageIndex];
                if (!double.IsFinite(item.X) || !double.IsFinite(item.Y)
                    || !double.IsFinite(item.Width) || !double.IsFinite(item.Height)
                    || !double.IsFinite(item.Rotation) || item.X < 0 || item.Y < 0
                    || item.Width <= 0 || item.Height <= 0
                    || item.X + item.Width > page.Width.Point || item.Y + item.Height > page.Height.Point)
                    throw new SignItDomainException(DomainErrorKind.Validation, "signature_slot_invalid", "Slot tanda tangan berada di luar halaman.");
                if (item.IsCompleted && item.QrImageBytes is not { Length: > 0 })
                    throw new SignItDomainException(DomainErrorKind.Validation, "signature_asset_missing", "Slot selesai harus memiliki QR.");
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                var state = gfx.Save();
                if (item.Rotation != 0)
                {
                    gfx.RotateAtTransform(item.Rotation, new XPoint(item.X + item.Width / 2, item.Y + item.Height / 2));
                }

                if (item.IsCompleted && item.QrImageBytes is { Length: > 0 })
                {
                    // Draw slot background/card
                    gfx.DrawRectangle(new XPen(XColor.FromArgb(180, 200, 220), 0.75),
                        new XSolidBrush(XColor.FromArgb(248, 250, 252)),
                        item.X, item.Y, item.Width, item.Height);

                    // Divide slot into QR area and text info area
                    // Reserve the bottom 44pt for labels so a 100pt slot cannot overflow.
                    var qrSize = Math.Min(72, Math.Min(item.Width - 8, item.Height - 48));
                    if (qrSize < 32)
                        throw new SignItDomainException(DomainErrorKind.Validation, "signature_slot_too_small",
                            "Slot terlalu kecil untuk QR dan identitas penandatangan.");
                    var qrX = item.X + (item.Width - qrSize) / 2;
                    var qrY = item.Y + 4;

                    var embeddingBytes = QrPdfImage.Normalize(item.QrImageBytes);
                    using var imgStream = new MemoryStream(embeddingBytes, 0, embeddingBytes.Length, false, true);
                    using var qrImage = XImage.FromStream(imgStream);
                    gfx.DrawImage(qrImage, qrX, qrY, qrSize, qrSize);

                    if (fontRegular != null && fontBold != null && fontSmall != null)
                    {
                        var textY = qrY + qrSize + 4;
                        var textRect = new XRect(item.X + 2, textY, item.Width - 4, 10);
                        DrawFitted(gfx, "Ditandatangani secara elektronik:", fontSmall, XBrushes.DarkSlateGray, textRect);

                        textRect = new XRect(item.X + 2, textY + 9, item.Width - 4, 10);
                        DrawFitted(gfx, item.SignerName, fontBold, XBrushes.Black, textRect);

                        if (!string.IsNullOrWhiteSpace(item.SignerPosition))
                        {
                            textRect = new XRect(item.X + 2, textY + 18, item.Width - 4, 10);
                            DrawFitted(gfx, item.SignerPosition, fontSmall, XBrushes.DarkGray, textRect);
                        }

                        if (item.SignedAt.HasValue)
                        {
                            var wibTime = TimeZoneInfo.ConvertTime(item.SignedAt.Value, WibTimeZone);
                            var formattedTime = $"{wibTime:dd/MM/yyyy HH:mm} WIB";
                            textRect = new XRect(item.X + 2, textY + 26, item.Width - 4, 10);
                            gfx.DrawString(formattedTime, fontSmall, XBrushes.SlateGray, textRect, XStringFormats.TopCenter);
                        }
                    }
                }
                else
                {
                    // Placeholder for pending signature slot
                    var dashedPen = new XPen(XColor.FromArgb(160, 175, 195), 1) { DashStyle = XDashStyle.Dash };
                    gfx.DrawRectangle(dashedPen, new XSolidBrush(XColor.FromArgb(250, 250, 250)),
                        item.X, item.Y, item.Width, item.Height);

                    if (fontRegular != null)
                    {
                        var textRect = new XRect(item.X + 2, item.Y + item.Height / 2 - 10, item.Width - 4, 10);
                        gfx.DrawString("[Menunggu Tanda Tangan]", fontRegular, XBrushes.SlateGray, textRect, XStringFormats.TopCenter);

                        textRect = new XRect(item.X + 2, item.Y + item.Height / 2, item.Width - 4, 10);
                        gfx.DrawString(item.SignerName, fontRegular, XBrushes.DarkGray, textRect, XStringFormats.TopCenter);
                    }
                }

                gfx.Restore(state);
            }

            // Draw document verification code footer if provided
            if (!string.IsNullOrWhiteSpace(documentVerificationCode) && document.PageCount > 0)
            {
                var lastPage = document.Pages[document.PageCount - 1];
                using var gfx = XGraphics.FromPdfPage(lastPage, XGraphicsPdfPageOptions.Append);
                if (fontSmall != null)
                {
                    var footerText = $"Kode verifikasi keaslian dokumen: {documentVerificationCode}";
                    gfx.DrawString(footerText, fontSmall, XBrushes.DarkSlateGray,
                        new XPoint(36, lastPage.Height.Point - 20));
                }
            }

            using var outputStream = new MemoryStream();
            document.Save(outputStream);
            return Task.FromResult(outputStream.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (SignItDomainException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "pdf_overlay_failed",
                $"Gagal melakukan overlay tanda tangan pada dokumen: {ex.Message}");
        }
    }

    private static void DrawFitted(XGraphics gfx, string text, XFont font, XBrush brush, XRect bounds)
    {
        var display = text;
        while (display.Length > 0 && gfx.MeasureString(display, font).Width > bounds.Width)
            display = display[..^1];
        if (display != text)
        {
            while (display.Length > 0 && gfx.MeasureString(display + "...", font).Width > bounds.Width)
                display = display[..^1];
            display += "...";
        }
        gfx.DrawString(display, font, brush, bounds, XStringFormats.TopCenter);
    }

    public byte[] GeneratePlaceholderPdf(string title, string letterNumber, string content)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using var gfx = XGraphics.FromPdfPage(page);

        XFont? fontTitle = null;
        XFont? fontSub = null;
        XFont? fontBody = null;
        try
        {
            fontTitle = new XFont("Arial", 14, XFontStyleEx.Bold);
            fontSub = new XFont("Arial", 10, XFontStyleEx.Regular);
            fontBody = new XFont("Arial", 10, XFontStyleEx.Regular);
        }
        catch { }

        // Draw header border
        gfx.DrawRectangle(XPens.DarkBlue, 36, 36, page.Width.Point - 72, 60);

        if (fontTitle != null)
        {
            gfx.DrawString(title, fontTitle, XBrushes.DarkBlue, new XPoint(50, 65));
            if (fontSub != null)
            {
                gfx.DrawString($"Nomor: {letterNumber}", fontSub, XBrushes.Black, new XPoint(50, 85));
            }
        }

        if (fontBody != null)
        {
            var lines = content.Split('\n');
            var y = 130.0;
            foreach (var line in lines)
            {
                gfx.DrawString(line, fontBody, XBrushes.Black, new XPoint(50, y));
                y += 18;
            }
        }

        using var stream = new MemoryStream();
        doc.Save(stream);
        return stream.ToArray();
    }
}
