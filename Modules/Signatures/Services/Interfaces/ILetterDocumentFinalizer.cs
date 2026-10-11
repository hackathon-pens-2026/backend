using SignIt.Modules.Letters.Models;

namespace SignIt.Modules.Signatures.Services;

public interface ILetterDocumentFinalizer
{
    Task<string> FinalizeDocumentAsync(LetterRequest letterRequest, LetterRevision revision, CancellationToken ct);
}
