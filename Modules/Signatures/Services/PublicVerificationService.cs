using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;

namespace SignIt.Modules.Signatures.Services;

public sealed class PublicVerificationService : IPublicVerificationService
{
    private const long MaxUploadSizeBytes = 10 * 1024 * 1024; // 10 MiB limit
    private readonly AppDbContext _db;

    public PublicVerificationService(AppDbContext db)
    {
        _db = db;
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
        if (fileSize <= 0 || fileSize > MaxUploadSizeBytes)
            throw new SignItDomainException(DomainErrorKind.Validation, "file_size_exceeded",
                "Ukuran file unggahan maksimal 10 MiB.");

        using var memory = new MemoryStream();
        await fileStream.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();

        // Magic bytes verification (%PDF)
        if (bytes.Length < 4 || bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46)
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_pdf_format",
                "File yang diunggah bukan merupakan dokumen PDF yang sah.");

        var computedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var record = await _db.VerificationRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.FinalHash == computedHash, ct);

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
