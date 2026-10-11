namespace SignIt.Modules.Signatures.Services;

public sealed record PdfInspectionResult(byte[] Bytes, string Sha256Hash);

public interface IPdfDocumentInspector
{
    Task<PdfInspectionResult> InspectPdfAsync(Stream fileStream, long fileSize, CancellationToken ct);
}
