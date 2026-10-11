using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public interface ISubmissionValidator
{
    void ValidateIdempotencyKey(string key);
    void ValidateDraftState(LetterRequest letter, SubmitLetterRequest request, bool hasExistingTasks);
    void ValidateContent(LetterRevision draft, string expectedContentHash);
    void ValidateTemplate(LetterRevision draft, LetterTemplateDto template, IReadOnlyDictionary<string, string> fields);
    void ValidatePreviewJob(LetterPreviewJob job, string renderedInputHash, SignatureSlot[]? requestSlots, SignatureSlot[] serverSlots);
    void ValidateReviewDocument(Document review, string expectedReviewHash, byte[]? bytes);
}
