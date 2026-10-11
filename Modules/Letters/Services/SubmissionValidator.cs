using System.Security.Cryptography;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Letters.Services;

public sealed class SubmissionValidator : ISubmissionValidator
{
    public void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80)
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "idempotency_key_required", "Idempotency-Key wajib diisi, maksimal 80 karakter.");
        }
    }

    public void ValidateDraftState(LetterRequest letter, SubmitLetterRequest request, bool hasExistingTasks)
    {
        if (!letter.CanBeSubmitted
            || letter.RowVersion != request.ExpectedVersion
            || letter.CurrentRevisionId != request.ExpectedRevisionId
            || hasExistingTasks)
        {
            throw new SignItDomainException(DomainErrorKind.Conflict, "stale_draft", "Draft berubah atau sudah diajukan. Muat ulang surat.");
        }
    }

    public void ValidateContent(LetterRevision draft, string expectedContentHash)
    {
        if (draft.ContentHash != expectedContentHash)
        {
            throw new SignItDomainException(DomainErrorKind.Conflict, "stale_content", "Isi draft telah berubah.");
        }
    }

    public void ValidateTemplate(LetterRevision draft, LetterTemplateDto template, IReadOnlyDictionary<string, string> fields)
    {
        if (draft.TemplateVersionId != $"{template.TemplateId}:{template.Version}")
        {
            throw new SignItDomainException(DomainErrorKind.Conflict, "stale_template", "Template berubah. Perbarui preview.");
        }

        if (template.Fields.Any(x => x.ValueSource == "user" && x.Required && (!fields.TryGetValue(x.Key, out var value) || string.IsNullOrWhiteSpace(value))))
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "required_fields_missing", "Lengkapi field wajib template sebelum mengajukan.");
        }
    }

    public void ValidatePreviewJob(LetterPreviewJob job, string renderedInputHash, SignatureSlot[]? requestSlots, SignatureSlot[] serverSlots)
    {
        if (job.InputHash != renderedInputHash)
        {
            throw new SignItDomainException(DomainErrorKind.Conflict, "stale_preview", "Peserta, fasilitas atau template berubah. Generate preview baru sebelum mengajukan.");
        }

        if (requestSlots is null || !requestSlots.OrderBy(x => x.PositionCode).SequenceEqual(serverSlots.OrderBy(x => x.PositionCode)))
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "preview_slots_mismatch", "Slot QR harus sesuai layout preview dari server.");
        }
    }

    public void ValidateReviewDocument(Document review, string expectedReviewHash, byte[]? bytes)
    {
        if (bytes == null || Hash(bytes) != review.Sha256 || review.Sha256 != expectedReviewHash)
        {
            throw new SignItDomainException(DomainErrorKind.Conflict, "review_hash_mismatch", "Preview PDF berubah atau tidak tersedia.");
        }
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
