namespace SignIt.Modules.Signatures.Services;

public sealed record PdfSignatureOverlayItem(
    int PageIndex,
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation,
    byte[]? QrImageBytes,
    string SignerName,
    string? SignerPosition,
    DateTimeOffset? SignedAt,
    bool IsCompleted);

public interface IPdfOverlayService
{
    Task<byte[]> OverlaySignaturesAsync(
        byte[] sourcePdfBytes,
        IReadOnlyList<PdfSignatureOverlayItem> items,
        string? documentVerificationCode = null,
        CancellationToken ct = default);

    byte[] GeneratePlaceholderPdf(string title, string letterNumber, string content);
}
