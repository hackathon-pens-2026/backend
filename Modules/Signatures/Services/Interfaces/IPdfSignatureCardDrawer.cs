using PdfSharp.Drawing;

namespace SignIt.Modules.Signatures.Services;

public interface IPdfSignatureCardDrawer
{
    void DrawCompletedCard(
        XGraphics gfx,
        PdfSignatureOverlayItem item,
        XFont? fontRegular,
        XFont? fontBold,
        XFont? fontSmall,
        TimeZoneInfo timeZone);

    void DrawPendingCard(
        XGraphics gfx,
        PdfSignatureOverlayItem item,
        XFont? fontRegular);

    void DrawVerificationFooter(
        XGraphics gfx,
        string verificationCode,
        XFont font,
        double yPosition);
}
