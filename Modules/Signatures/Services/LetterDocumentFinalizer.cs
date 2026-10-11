using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Templates.Services;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Modules.Signatures.Services;

public sealed class LetterDocumentFinalizer : ILetterDocumentFinalizer
{
    private readonly AppDbContext _db;
    private readonly IStorageService _storage;
    private readonly IQrCodeGenerator _qrGenerator;
    private readonly IPdfOverlayService _pdfOverlay;
    private readonly WorkflowEmailService _emails;
    private readonly TimeProvider _clock;
    private readonly ILogger<LetterDocumentFinalizer> _logger;

    public LetterDocumentFinalizer(
        AppDbContext db,
        IStorageService storage,
        IQrCodeGenerator qrGenerator,
        IPdfOverlayService pdfOverlay,
        WorkflowEmailService emails,
        TimeProvider clock,
        ILogger<LetterDocumentFinalizer> logger)
    {
        _db = db;
        _storage = storage;
        _qrGenerator = qrGenerator;
        _pdfOverlay = pdfOverlay;
        _emails = emails;
        _clock = clock;
        _logger = logger;
    }

    public async Task<string> FinalizeDocumentAsync(LetterRequest letterRequest, LetterRevision revision, CancellationToken ct)
    {
        if (letterRequest.Status != LetterStatus.Finalizing) letterRequest.MarkFinalizing();
        var now = _clock.GetUtcNow();
        try
        {
            var tasks = await _db.WorkflowTasks.AsNoTracking().Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
            if (letterRequest.CurrentRevisionId != revision.Id || tasks.Count == 0
                || tasks.Any(x => !x.IsDone))
                throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "workflow_incomplete", "Semua tugas revisi harus selesai sebelum finalisasi.");

            byte[]? basePdfBytes = null;
            if (revision.ReviewDocumentId.HasValue)
            {
                var review = await _db.Documents.SingleOrDefaultAsync(x => x.Id == revision.ReviewDocumentId.Value, ct);
                if (review != null)
                {
                    basePdfBytes = await _storage.ReadBytesAsync(review.StorageKey, ct);
                    if (basePdfBytes != null && (basePdfBytes.LongLength != review.Bytes
                        || !string.Equals(_qrGenerator.ComputeSha256(basePdfBytes), review.Sha256, StringComparison.OrdinalIgnoreCase)))
                        throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "review_document_invalid", "Dokumen review berubah atau rusak.");
                }
            }
            if (basePdfBytes == null || basePdfBytes.Length == 0)
                throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "review_document_missing",
                    "Dokumen yang telah ditinjau tidak tersedia.");

            var participants = await _db.LetterParticipants.Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
            using (var snapshot = JsonDocument.Parse(revision.DataJson))
            {
                if (snapshot.RootElement.TryGetProperty("PreviewJobId", out var jobElement))
                {
                    if (_qrGenerator.ComputeSha256(Encoding.UTF8.GetBytes(revision.DataJson)) != revision.ContentHash)
                        throw new InvalidOperationException("Snapshot revisi berubah.");
                    var jobId = jobElement.GetGuid();
                    var job = await _db.LetterPreviewJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, ct);
                    if (job.RequestId != letterRequest.Id || job.DocumentId != revision.ReviewDocumentId || job.State != "Ready"
                        || _qrGenerator.ComputeSha256(Encoding.UTF8.GetBytes(job.InputJson)) != job.InputHash)
                        throw new InvalidOperationException("Snapshot renderer tidak sesuai dokumen review.");
                    var input = JsonSerializer.Deserialize<PreviewRenderInput>(job.InputJson)
                        ?? throw new InvalidOperationException("Snapshot renderer kosong.");
                    var renderer = new PdfSharpLetterTemplateRenderer();
                    var reconstructed = renderer.Render(input, ct);
                    var rendered = renderer.RenderFinal(input, letterRequest.Number, ct);
                    if (JsonSerializer.Serialize(reconstructed.Slots) != job.SlotsJson
                        || !rendered.Slots.SequenceEqual(reconstructed.Slots)
                        || rendered.Slots.Length != participants.Count
                        || participants.Any(p => !rendered.Slots.Any(s => s.PositionCode == p.SlotKey
                            && s.PageIndex == p.PageIndex && s.X == p.X && s.Y == p.Y && s.Width == p.Width && s.Height == p.Height)))
                        throw new InvalidOperationException("Layout final mengubah slot yang disetujui.");
                    basePdfBytes = rendered.Bytes;
                }
            }
            var evidences = await _db.SignatureEvidences.Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
            if (tasks.Where(t => t.ActionType is WorkflowActionType.Sign or WorkflowActionType.ApproveAndSign)
                .Any(t => evidences.Count(e => e.TaskId == t.Id && e.ActorId == t.ActedByUserId
                    && string.Equals(e.ContentHash, revision.ContentHash, StringComparison.OrdinalIgnoreCase)) != 1))
                throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "signature_evidence_invalid", "Evidence semua tugas tanda tangan wajib tersedia dan sesuai revisi.");
            var signatureDocuments = new SignatureDocumentService(_db, _storage, _qrGenerator, _pdfOverlay);
            var overlays = await signatureDocuments.BuildOverlaysAsync(revision, true, ct);

            var verificationCode = $"SIG-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            var finalPdf = await _pdfOverlay.OverlaySignaturesAsync(basePdfBytes, overlays, verificationCode, ct);
            var finalHash = Convert.ToHexStringLower(SHA256.HashData(finalPdf));
            var storageKey = $"documents/{letterRequest.Id}/{revision.Id}/final-{Guid.NewGuid():N}.pdf";
            await _storage.SaveAsync(storageKey, finalPdf, "application/pdf", ct);
            var finalDocumentId = Guid.NewGuid();
            _db.Documents.Add(Document.Create(finalDocumentId, revision.Id, DocumentKind.Final, storageKey,
                "application/pdf", finalPdf.Length, finalHash, now));
            _db.VerificationRecords.Add(VerificationRecord.Create(Guid.NewGuid(), letterRequest.Id,
                finalDocumentId, verificationCode, finalHash, now));
            revision.SetFinalDocument(finalDocumentId);
            letterRequest.MarkCompleted(now);
            _db.AuditLogs.Add(AuditLog.Record(null, "letter.completed", "LetterRequest", letterRequest.Id,
                revision.Id, now, Guid.NewGuid().ToString("N"),
                $"Surat selesai dengan kode verifikasi {verificationCode} dan SHA-256 {finalHash}"));
            var submitter = await _db.Users.AsNoTracking().SingleAsync(x => x.Id == letterRequest.SubmittedByUserId, ct);
            await _emails.EnqueueCompletedAsync(letterRequest, submitter, ct);
            _logger.LogInformation("Letter {LetterId} finalized successfully. Code: {VerificationCode}",
                letterRequest.Id, verificationCode);
            return verificationCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize document for letter {LetterId}", letterRequest.Id);
            letterRequest.MarkProcessingFailed();
            _db.AuditLogs.Add(AuditLog.Record(null, "letter.processing_failed", "LetterRequest",
                letterRequest.Id, revision.Id, now, Guid.NewGuid().ToString("N"),
                $"Finalisasi gagal: {ex.Message}"));
            return string.Empty;
        }
    }
}
