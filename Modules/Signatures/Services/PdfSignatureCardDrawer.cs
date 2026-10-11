using PdfSharp.Drawing;
using SignIt.Infrastructure.Errors;

namespace SignIt.Modules.Signatures.Services;

public sealed class PdfSignatureCardDrawer : IPdfSignatureCardDrawer
{
    public void DrawCompletedCard(
        XGraphics gfx,
        PdfSignatureOverlayItem item,
        XFont? fontRegular,
        XFont? fontBold,
        XFont? fontSmall,
        TimeZoneInfo timeZone)
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

        var embeddingBytes = QrPdfImage.Normalize(item.QrImageBytes!);
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
                var wibTime = TimeZoneInfo.ConvertTime(item.SignedAt.Value, timeZone);
                var formattedTime = $"{wibTime:dd/MM/yyyy HH:mm} WIB";
                textRect = new XRect(item.X + 2, textY + 26, item.Width - 4, 10);
                gfx.DrawString(formattedTime, fontSmall, XBrushes.SlateGray, textRect, XStringFormats.TopCenter);
            }
        }
    }

    public void DrawPendingCard(XGraphics gfx, PdfSignatureOverlayItem item, XFont? fontRegular)
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

    public void DrawVerificationFooter(XGraphics gfx, string verificationCode, XFont font, double yPosition)
    {
        var footerText = $"Kode verifikasi keaslian dokumen: {verificationCode}";
        gfx.DrawString(footerText, font, XBrushes.DarkSlateGray, new XPoint(36, yPosition));
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
}
