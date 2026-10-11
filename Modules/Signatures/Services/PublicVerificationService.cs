using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;

namespace SignIt.Modules.Signatures.Services;

public sealed class PublicVerificationService : IPublicVerificationService
{
    private readonly AppDbContext _db;
    private readonly IPdfDocumentInspector _inspector;

    public PublicVerificationService(AppDbContext db, IPdfDocumentInspector? inspector = null)
    {
        _db = db;
        _inspector = inspector ?? new PdfDocumentInspector();
    }

    public async Task<PublicVerificationDto> VerifyByCodeAsync(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_code", "Kode verifikasi tidak boleh kosong.");

        var normalizedCode = code.Trim().ToUpperInvariant();
        var record = await _db.VerificationRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.RandomCode == normalizedCode, ct);

        if (record == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "document_not_found", "Dokumen tidak ditemukan atau kode verifikasi tidak valid.");

        var letterRequest = await _db.LetterRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == record.RequestId, ct);

        if (letterRequest == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Dokumen surat tidak ditemukan.");

        var evidences = letterRequest.CurrentRevisionId.HasValue
            ? await _db.SignatureEvidences
                .AsNoTracking()
                .Where(x => x.RevisionId == letterRequest.CurrentRevisionId.Value)
                .OrderBy(x => x.SignedAt)
                .ToListAsync(ct)
            : [];

        var actorIds = evidences.Select(x => x.ActorId).Distinct().ToList();
        var users = await _db.Users
            .AsNoTracking()
            .Where(x => actorIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var signers = evidences.Select(e => new SignerSummaryDto(
            Name: users.TryGetValue(e.ActorId, out var name) ? name : "Penandatangan Terverifikasi",
            Role: e.Role,
            Position: e.PositionSnapshot,
            SignedAt: e.SignedAt
        )).ToList();

        var isRevoked = record.Status == VerificationStatus.Revoked;

        return new PublicVerificationDto(
            VerificationCode: record.RandomCode,
            Status: record.Status.ToString(),
            LetterNumber: letterRequest.Number,
            LetterTitle: letterRequest.Title,
            LetterType: letterRequest.TypeId,
            PublishedAt: record.PublishedAt,
            IsRevoked: isRevoked,
            RevokedAt: record.RevokedAt,
            RevocationReason: record.RevocationReason,
            FileSha256Fingerprint: record.FinalHash,
            Signers: signers);
    }

    public async Task<VerifyUploadResultDto> VerifyByUploadAsync(Stream fileStream, long fileSize, CancellationToken ct)
    {
        var inspection = await _inspector.InspectPdfAsync(fileStream, fileSize, ct);

        var record = await _db.VerificationRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.FinalHash == inspection.Sha256Hash, ct);

        if (record == null)
        {
            return new VerifyUploadResultDto(
                Matches: false,
                VerificationCode: null,
                Details: null,
                Message: "Dokumen tidak cocok dengan arsip surat atau telah mengalami perubahan isi.");
        }

        var details = await VerifyByCodeAsync(record.RandomCode, ct);
        return new VerifyUploadResultDto(
            Matches: true,
            VerificationCode: record.RandomCode,
            Details: details,
            Message: "Dokumen terverifikasi asli dan cocok dengan arsip resmi sistem.");
    }
}
