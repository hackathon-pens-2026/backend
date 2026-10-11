using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SignIt.Infrastructure.Errors;

namespace SignIt.Modules.Signatures.Services;

public sealed class PdfSharpOverlayService : IPdfOverlayService
{
    private static readonly TimeZoneInfo WibTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Jakarta");

    private readonly IPdfSignatureCardDrawer _cardDrawer;

    public PdfSharpOverlayService(IPdfSignatureCardDrawer? cardDrawer = null)
    {
        _cardDrawer = cardDrawer ?? new PdfSignatureCardDrawer();
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
                    _cardDrawer.DrawCompletedCard(gfx, item, fontRegular, fontBold, fontSmall, WibTimeZone);
                }
                else
                {
                    _cardDrawer.DrawPendingCard(gfx, item, fontRegular);
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
                    _cardDrawer.DrawVerificationFooter(gfx, documentVerificationCode, fontSmall, lastPage.Height.Point - 20);
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
