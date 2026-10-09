using SignIt.Modules.Signatures.Services;

namespace SignIt.Signatures.Tests;

// Isolates workflow persistence from the real PDF/PNG decoder (feature #3).
// Establishes no visual QR overlay correctness; never used by production DI.
internal sealed class WorkflowTestPdfAdapter(bool fail = false) : IPdfOverlayService
{
    public Task<byte[]> OverlaySignaturesAsync(byte[] sourcePdfBytes, IReadOnlyList<PdfSignatureOverlayItem> items,
        string? documentVerificationCode = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (fail) throw new InvalidOperationException("Injected PDF adapter failure");
        if (items.Any(x => !x.IsCompleted || x.QrImageBytes is not { Length: > 0 }))
            throw new InvalidOperationException("Missing completed evidence");
        return Task.FromResult(sourcePdfBytes);
    }
    public byte[] GeneratePlaceholderPdf(string title, string letterNumber, string content) => throw new NotSupportedException();
}
