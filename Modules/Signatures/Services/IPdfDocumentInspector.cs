using System.Security.Cryptography;
using SignIt.Infrastructure.Errors;

namespace SignIt.Modules.Signatures.Services;

public sealed record PdfInspectionResult(byte[] Bytes, string Sha256Hash);

public interface IPdfDocumentInspector
{
    Task<PdfInspectionResult> InspectPdfAsync(Stream fileStream, long fileSize, CancellationToken ct);
}

public sealed class PdfDocumentInspector : IPdfDocumentInspector
{
    public const long DefaultMaxUploadSizeBytes = 10 * 1024 * 1024; // 10 MiB limit

    private readonly long _maxSizeBytes;

    public PdfDocumentInspector(long maxSizeBytes = DefaultMaxUploadSizeBytes)
    {
        _maxSizeBytes = maxSizeBytes;
    }

    public async Task<PdfInspectionResult> InspectPdfAsync(Stream fileStream, long fileSize, CancellationToken ct)
    {
        if (fileSize <= 0 || fileSize > _maxSizeBytes)
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "file_size_exceeded",
                "Ukuran file unggahan maksimal 10 MiB.");
        }

        using var memory = new MemoryStream();
        await fileStream.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();

        // Magic bytes verification (%PDF: 0x25, 0x50, 0x44, 0x46)
        if (bytes.Length < 4 || bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46)
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_pdf_format",
                "File yang diunggah bukan merupakan dokumen PDF yang sah.");
        }

        var computedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new PdfInspectionResult(bytes, computedHash);
    }
}
