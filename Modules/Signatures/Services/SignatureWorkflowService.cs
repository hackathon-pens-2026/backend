using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Signatures.Services;

public sealed class SignatureWorkflowService : ISignatureWorkflowService
{
    private readonly AppDbContext _db;
    private readonly IUserSignatureQrService _qrService;
    private readonly IQrCodeGenerator _qrGenerator;
    private readonly IPdfOverlayService _pdfOverlay;
    private readonly IStorageService _storage;
    private readonly TimeProvider _clock;
    private readonly ILogger<SignatureWorkflowService> _logger;

    public SignatureWorkflowService(
        AppDbContext db,
        IUserSignatureQrService qrService,
        IQrCodeGenerator qrGenerator,
        IPdfOverlayService pdfOverlay,
        IStorageService storage,
        TimeProvider clock,
        ILogger<SignatureWorkflowService> logger)
    {
        _db = db;
        _qrService = qrService;
        _qrGenerator = qrGenerator;
        _pdfOverlay = pdfOverlay;
        _storage = storage;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SignTaskResultDto> ExecuteTaskActionAsync(
        Guid taskId,
        Guid actorUserId,
        WorkflowActionType expectedActionType,
        SignTaskRequest request,
        string? idempotencyKey,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // 1. Idempotency Check
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existingAttempt = await _db.SigningAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TaskId == taskId && x.ActorId == actorUserId && x.IdempotencyKey == idempotencyKey, ct);

            if (existingAttempt != null && existingAttempt.Status == "Success")
            {
                var existingEvidence = await _db.SignatureEvidences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TaskId == taskId, ct);

                if (existingEvidence != null)
                {
                    var task = await _db.WorkflowTasks.AsNoTracking().SingleAsync(x => x.Id == taskId, ct);
                    var letterReq = await _db.LetterRequests.AsNoTracking().FirstOrDefaultAsync(x => x.CurrentRevisionId == task.RevisionId, ct);
                    var verRecord = letterReq != null
                        ? await _db.VerificationRecords.AsNoTracking().FirstOrDefaultAsync(x => x.RequestId == letterReq.Id, ct)
                        : null;

                    return new SignTaskResultDto(
                        taskId,
                        task.Status,
                        existingEvidence.SignedAt,
                        existingEvidence.Id,
                        existingEvidence.Role,
                        existingEvidence.PositionSnapshot,
                        existingEvidence.ContentHash,
                        existingEvidence.QrHash,
                        letterReq?.Status == LetterStatus.Completed,
                        verRecord?.RandomCode);
                }
            }
        }

        // 2. Load Task
        var currentTask = await _db.WorkflowTasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (currentTask == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "task_not_found", "Tugas workflow tidak ditemukan.");

        if (currentTask.Status != WorkflowTaskStatus.Active)
            throw new SignItDomainException(DomainErrorKind.Conflict, "task_not_active",
                $"Tugas tidak dalam status aktif (status saat ini: {currentTask.Status}).");

        if (currentTask.ActionType != expectedActionType)
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_action_type",
                $"Tipe aksi {expectedActionType} tidak cocok dengan tugas ini ({currentTask.ActionType}).");

        // 3. Authorize Actor (Direct assignment or active delegation)
        Guid? delegatedFromUserId = null;
        string? mandateDescription = null;

        if (currentTask.AssignedUserId != actorUserId)
        {
            var activeDelegation = await _db.Delegations
                .FirstOrDefaultAsync(d => d.FromUserId == currentTask.AssignedUserId && d.ToUserId == actorUserId
                    && d.IsActive && d.StartAt <= now && d.EndAt >= now, ct);

            if (activeDelegation == null)
                throw new SignItDomainException(DomainErrorKind.Forbidden, "forbidden_task_actor",
                    "Anda tidak memiliki wewenang atau delegasi aktif untuk menyelesaikan tugas ini.");

            delegatedFromUserId = currentTask.AssignedUserId;
            var assignedUser = await _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == currentTask.AssignedUserId, ct);
            mandateDescription = assignedUser != null ? $"a.n. {assignedUser.Name}" : "a.n. Pejabat Berwenang";
        }

        // 4. Concurrency & Content Verification
        var revision = await _db.LetterRevisions.SingleOrDefaultAsync(x => x.Id == currentTask.RevisionId, ct);
        if (revision == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "revision_not_found", "Revisi surat tidak ditemukan.");

        if (revision.Id != request.ExpectedRevisionId || !string.Equals(revision.ContentHash, request.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SignItDomainException(DomainErrorKind.Conflict, "revision_hash_mismatch",
                "Revisi dokumen telah berubah. Muat ulang halaman dan tinjau kembali sebelum menandatangani.");

        var letterRequest = await _db.LetterRequests.SingleOrDefaultAsync(x => x.Id == revision.RequestId, ct);
        if (letterRequest == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "letter_request_not_found", "Pengajuan surat tidak ditemukan.");

        if (letterRequest.CurrentRevisionId != revision.Id || letterRequest.Status != LetterStatus.InProgress)
            throw new SignItDomainException(DomainErrorKind.Conflict, "inactive_letter_revision",
                "Revisi ini tidak lagi aktif untuk ditandatangani. Muat ulang pengajuan surat.");

        // 5. Retrieve or Lazy-provision Actor's QR
        var actorQr = await _qrService.EnsureActiveEntityAsync(actorUserId, ct);

        // 6. Slot & Position snapshot
        LetterParticipant? participant = null;
        if (currentTask.ParticipantId.HasValue)
        {
            participant = await _db.LetterParticipants.SingleOrDefaultAsync(x => x.Id == currentTask.ParticipantId.Value, ct);
        }

        var actorUser = await _db.Users.AsNoTracking().SingleAsync(x => x.Id == actorUserId, ct);
        var actorAssignment = await _db.Assignments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == actorUserId && x.IsActive, ct);

        var roleSnapshot = participant?.Role.ToString() ?? currentTask.ActionType.ToString();
        var positionSnapshot = participant?.PositionSnapshot ?? actorAssignment?.PositionName;

        // 7. Persist Evidence & SigningAttempt
        var evidenceId = Guid.NewGuid();
        var evidence = SignatureEvidence.Create(
            evidenceId,
            currentTask.Id,
            revision.Id,
            actorUserId,
            roleSnapshot,
            positionSnapshot,
            revision.ContentHash,
            actorQr.Id,
            actorQr.Version,
            actorQr.ImageSha256,
            now,
            delegatedFromUserId,
            mandateDescription,
            ipAddress,
            userAgent);

        _db.SignatureEvidences.Add(evidence);

        var attempt = SigningAttempt.Create(
            Guid.NewGuid(),
            currentTask.Id,
            revision.Id,
            actorUserId,
            actorQr.Id,
            actorQr.Version,
            actorQr.ImageSha256,
            revision.ContentHash,
            "Success",
            idempotencyKey,
            now);

        _db.SigningAttempts.Add(attempt);

        // 8. Complete Task
        var targetStatus = currentTask.ActionType switch
        {
            WorkflowActionType.Sign => WorkflowTaskStatus.Signed,
            WorkflowActionType.ApproveAndSign => WorkflowTaskStatus.Approved,
            WorkflowActionType.Acknowledge => WorkflowTaskStatus.Acknowledged,
            _ => WorkflowTaskStatus.Signed
        };

        currentTask.Complete(targetStatus, actorUserId, now, request.Comment);

        // 9. Audit Log
        _db.AuditLogs.Add(AuditLog.Record(
            actorUserId,
            $"task.{targetStatus.ToString().ToLowerInvariant()}",
            "WorkflowTask",
            currentTask.Id,
            revision.Id,
            now,
            correlationId: Guid.NewGuid().ToString("N"),
            details: $"Tindakan {currentTask.ActionType} berhasil oleh {actorUser.Name}",
            ipAddress: ipAddress,
            userAgent: userAgent));

        // 10. Sequential Workflow Progression
        var allRevisionTasks = await _db.WorkflowTasks
            .Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.Order)
            .ToListAsync(ct);

        var remainingTasks = allRevisionTasks
            .Where(x => x.Id != currentTask.Id && x.Status != WorkflowTaskStatus.Signed
                && x.Status != WorkflowTaskStatus.Approved && x.Status != WorkflowTaskStatus.Acknowledged)
            .OrderBy(x => x.Order)
            .ToList();

        var isWorkflowCompleted = false;
        string? verificationCode = null;

        if (remainingTasks.Count > 0)
        {
            // Activate the next task in sequence if not already active
            var nextTask = remainingTasks.FirstOrDefault(x => x.Status == WorkflowTaskStatus.Pending);
            if (nextTask != null)
            {
                nextTask.Activate(now);
                _db.AuditLogs.Add(AuditLog.Record(
                    null,
                    "task.activated",
                    "WorkflowTask",
                    nextTask.Id,
                    revision.Id,
                    now,
                    correlationId: Guid.NewGuid().ToString("N"),
                    details: $"Tugas berikutnya diaktifkan: Order {nextTask.Order} untuk user {nextTask.AssignedUserId}"));
            }
        }
        else
        {
            // All mandatory tasks completed! Finalize document.
            verificationCode = await FinalizeDocumentInternalAsync(letterRequest, revision, ct);
            isWorkflowCompleted = letterRequest.Status == LetterStatus.Completed;
        }

        await _db.SaveChangesAsync(ct);

        return new SignTaskResultDto(
            currentTask.Id,
            currentTask.Status,
            now,
            evidence.Id,
            evidence.Role,
            evidence.PositionSnapshot,
            evidence.ContentHash,
            evidence.QrHash,
            isWorkflowCompleted,
            verificationCode);
    }

    public async Task RejectTaskAsync(
        Guid taskId,
        Guid actorUserId,
        string comment,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var task = await _db.WorkflowTasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "task_not_found", "Tugas tidak ditemukan.");

        if (task.Status != WorkflowTaskStatus.Active)
            throw new SignItDomainException(DomainErrorKind.Conflict, "task_not_active", "Tugas tidak dalam status aktif.");

        await AuthorizeTaskActorAsync(task, actorUserId, now, ct);

        var revision = await _db.LetterRevisions.SingleOrDefaultAsync(x => x.Id == task.RevisionId, ct);
        var letterRequest = revision != null
            ? await _db.LetterRequests.SingleOrDefaultAsync(x => x.Id == revision.RequestId, ct)
            : null;

        task.Reject(actorUserId, now, comment);
        letterRequest?.MarkRejected();

        _db.AuditLogs.Add(AuditLog.Record(
            actorUserId,
            "task.rejected",
            "WorkflowTask",
            task.Id,
            task.RevisionId,
            now,
            correlationId: Guid.NewGuid().ToString("N"),
            details: $"Penolakan surat dengan alasan: {comment}",
            ipAddress: ipAddress,
            userAgent: userAgent));

        await _db.SaveChangesAsync(ct);
    }

    public async Task RequestRevisionTaskAsync(
        Guid taskId,
        Guid actorUserId,
        string comment,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var task = await _db.WorkflowTasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "task_not_found", "Tugas tidak ditemukan.");

        if (task.Status != WorkflowTaskStatus.Active)
            throw new SignItDomainException(DomainErrorKind.Conflict, "task_not_active", "Tugas tidak dalam status aktif.");

        await AuthorizeTaskActorAsync(task, actorUserId, now, ct);

        var revision = await _db.LetterRevisions.SingleOrDefaultAsync(x => x.Id == task.RevisionId, ct);
        var letterRequest = revision != null
            ? await _db.LetterRequests.SingleOrDefaultAsync(x => x.Id == revision.RequestId, ct)
            : null;

        task.RequestRevision(actorUserId, now, comment);
        letterRequest?.MarkNeedsRevision();

        _db.AuditLogs.Add(AuditLog.Record(
            actorUserId,
            "task.revision_requested",
            "WorkflowTask",
            task.Id,
            task.RevisionId,
            now,
            correlationId: Guid.NewGuid().ToString("N"),
            details: $"Permintaan revisi dengan catatan: {comment}",
            ipAddress: ipAddress,
            userAgent: userAgent));

        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> RetryFinalizationAsync(Guid requestId, CancellationToken ct)
    {
        var letterRequest = await _db.LetterRequests.SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (letterRequest == null) return false;
        if (letterRequest.Status != LetterStatus.ProcessingFailed && letterRequest.Status != LetterStatus.Finalizing)
            return false;

        var revision = await _db.LetterRevisions.SingleOrDefaultAsync(x => x.Id == letterRequest.CurrentRevisionId, ct);
        if (revision == null) return false;

        await FinalizeDocumentInternalAsync(letterRequest, revision, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task AuthorizeTaskActorAsync(WorkflowTask task, Guid actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        if (task.AssignedUserId == actorUserId) return;

        var activeDelegation = await _db.Delegations
            .AnyAsync(d => d.FromUserId == task.AssignedUserId && d.ToUserId == actorUserId
                && d.IsActive && d.StartAt <= now && d.EndAt >= now, ct);

        if (!activeDelegation)
            throw new SignItDomainException(DomainErrorKind.Forbidden, "forbidden_task_actor",
                "Anda tidak memiliki wewenang atau delegasi aktif untuk tugas ini.");
    }

    private async Task<string> FinalizeDocumentInternalAsync(
        LetterRequest letterRequest,
        LetterRevision revision,
        CancellationToken ct)
    {
        letterRequest.MarkFinalizing();
        var now = _clock.GetUtcNow();

        try
        {
            // 1. Get base PDF
            byte[]? basePdfBytes = null;
            if (revision.ReviewDocumentId.HasValue)
            {
                var reviewDoc = await _db.Documents.SingleOrDefaultAsync(x => x.Id == revision.ReviewDocumentId.Value, ct);
                if (reviewDoc != null)
                {
                    basePdfBytes = await _storage.ReadBytesAsync(reviewDoc.StorageKey, ct);
                }
            }

            if (basePdfBytes == null || basePdfBytes.Length == 0)
            {
                throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "review_document_missing",
                    "Dokumen yang telah ditinjau tidak tersedia. Pulihkan dokumen sebelum mencoba finalisasi kembali.");
            }

            // 2. Load participants and completed evidences
            var participants = await _db.LetterParticipants
                .Where(x => x.RevisionId == revision.Id)
                .ToListAsync(ct);

            var evidences = await _db.SignatureEvidences
                .Where(x => x.RevisionId == revision.Id)
                .ToListAsync(ct);

            var overlayItems = new List<PdfSignatureOverlayItem>();

            foreach (var participant in participants)
            {
                var participantTask = await _db.WorkflowTasks.SingleOrDefaultAsync(
                    x => x.RevisionId == revision.Id && x.ParticipantId == participant.Id, ct);
                var evidence = participantTask == null ? null : evidences.SingleOrDefault(x => x.TaskId == participantTask.Id);
                byte[]? qrBytes = null;

                if (evidence != null)
                {
                    var qr = await _db.SignatureQrs.SingleOrDefaultAsync(x => x.Id == evidence.QrAssetId, ct);
                    if (qr != null)
                    {
                        qrBytes = await _storage.ReadBytesAsync(qr.PrivateStorageKey, ct);
                        if (qrBytes == null || !string.Equals(_qrGenerator.ComputeSha256(qrBytes), evidence.QrHash, StringComparison.OrdinalIgnoreCase))
                            throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "signature_asset_invalid",
                                "Snapshot aset tanda tangan tidak tersedia atau telah berubah.");
                    }
                }

                if (evidence == null || qrBytes == null)
                    throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "signature_evidence_missing",
                        "Bukti tanda tangan peserta belum lengkap.");

                overlayItems.Add(new PdfSignatureOverlayItem(
                    participant.PageIndex,
                    participant.X,
                    participant.Y,
                    participant.Width,
                    participant.Height,
                    participant.Rotation,
                    qrBytes,
                    participant.DisplayNameSnapshot,
                    participant.PositionSnapshot,
                    evidence?.SignedAt,
                    IsCompleted: evidence != null));
            }

            // 3. Generate verification random code
            var randomCode = $"SIG-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";

            // 4. Perform overlay
            var finalPdfBytes = await _pdfOverlay.OverlaySignaturesAsync(basePdfBytes, overlayItems, randomCode, ct);
            var finalHash = Convert.ToHexStringLower(SHA256.HashData(finalPdfBytes));

            // 5. Store final document
            var storageKey = $"documents/{letterRequest.Id}/{revision.Id}/final.pdf";
            await _storage.SaveAsync(storageKey, finalPdfBytes, "application/pdf", ct);

            var finalDocId = Guid.NewGuid();
            var finalDoc = Document.Create(
                finalDocId,
                revision.Id,
                DocumentKind.Final,
                storageKey,
                "application/pdf",
                finalPdfBytes.Length,
                finalHash,
                now);

            _db.Documents.Add(finalDoc);

            // 6. Record verification
            var verRecord = VerificationRecord.Create(
                Guid.NewGuid(),
                letterRequest.Id,
                finalDocId,
                randomCode,
                finalHash,
                now);

            _db.VerificationRecords.Add(verRecord);

            // 7. Complete revision and letter
            revision.SetFinalDocument(finalDocId);
            letterRequest.MarkCompleted(now);

            _db.AuditLogs.Add(AuditLog.Record(
                null,
                "letter.completed",
                "LetterRequest",
                letterRequest.Id,
                revision.Id,
                now,
                correlationId: Guid.NewGuid().ToString("N"),
                details: $"Surat selesai dengan kode verifikasi {randomCode} dan SHA-256 {finalHash}"));

            _logger.LogInformation("Letter {LetterId} finalized successfully. Code: {VerificationCode}",
                letterRequest.Id, randomCode);

            return randomCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize document for letter {LetterId}", letterRequest.Id);
            letterRequest.MarkProcessingFailed();
            _db.AuditLogs.Add(AuditLog.Record(
                null,
                "letter.processing_failed",
                "LetterRequest",
                letterRequest.Id,
                revision.Id,
                now,
                correlationId: Guid.NewGuid().ToString("N"),
                details: $"Finalisasi gagal: {ex.Message}"));
            return string.Empty;
        }
    }
}
