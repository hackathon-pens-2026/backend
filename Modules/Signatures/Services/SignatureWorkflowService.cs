using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Modules.Signatures.Services;

public sealed class SignatureWorkflowService : ISignatureWorkflowService
{
    private readonly AppDbContext _db;
    private readonly IUserSignatureQrService _qrService;
    private readonly IQrCodeGenerator _qrGenerator;
    private readonly IPdfOverlayService _pdfOverlay;
    private readonly IStorageService _storage;
    private readonly RoomReservationService? _roomReservations;
    private readonly TimeProvider _clock;
    private readonly ILogger<SignatureWorkflowService> _logger;
    private readonly WorkflowTaskAccess _access;

    public SignatureWorkflowService(
        AppDbContext db,
        IUserSignatureQrService qrService,
        IQrCodeGenerator qrGenerator,
        IPdfOverlayService pdfOverlay,
        IStorageService storage,
        TimeProvider clock,
        ILogger<SignatureWorkflowService> logger,
        WorkflowTaskAccess access,
        RoomReservationService? roomReservations = null)
    {
        _db = db;
        _qrService = qrService;
        _qrGenerator = qrGenerator;
        _pdfOverlay = pdfOverlay;
        _storage = storage;
        _clock = clock;
        _logger = logger;
        _access = access;
        _roomReservations = roomReservations;
    }

    public async Task<SignTaskResultDto> ExecuteTaskActionAsync(
        Guid taskId, Guid actorUserId, WorkflowActionType expectedActionType, SignTaskRequest request,
        string? idempotencyKey, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var operation = $"task.execute.{expectedActionType}";
        var correlation = WorkflowIdempotency.Correlation(actorUserId, taskId, operation, idempotencyKey);
        if (request.Comment?.Length > 1000
            || request.Comment?.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') == true)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_comment", "Catatan maksimal 1000 karakter.");

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        var context = await _access.LoadAsync(taskId, true, ct);
        var delegation = await _access.AuthorizeAsync(context, actorUserId, ct);
        var currentTask = context.Task;
        var revision = context.Revision;
        var letterRequest = context.Letter;
        var replay = await WorkflowIdempotency.ReplayAsync<SignTaskResultDto>(
            _db, taskId, operation, correlation, request, ct);
        if (replay != null)
        {
            if (transaction != null) await transaction.CommitAsync(ct);
            return replay;
        }

        await _access.ValidateAsync(context, request.ExpectedRevisionId, request.ExpectedContentHash,
            request.ExpectedTaskVersion, WorkflowTaskStatus.Active, ct);
        if (currentTask.ActionType != expectedActionType || expectedActionType == WorkflowActionType.Review)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_action_type", "Aksi tidak cocok dengan tugas.");

        var participant = currentTask.ParticipantId.HasValue
            ? await _db.LetterParticipants.SingleAsync(x => x.Id == currentTask.ParticipantId.Value, ct)
            : null;
        if (expectedActionType == WorkflowActionType.Acknowledge && participant?.Required == true)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "signature_required",
                "Acknowledgement tidak menggantikan peserta tanda tangan wajib.");

        var now = _clock.GetUtcNow();
        var allTasksBeforeAction = await _db.WorkflowTasks
            .Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync(ct);
        var remainingTasks = allTasksBeforeAction
            .Where(x => x.Id != currentTask.Id && !WorkflowTaskAccess.IsDone(x.Status)).ToList();

        // The rooms module owns the PostgreSQL exclusion constraint. This confirmation
        // participates in the same transaction before the last approval is accepted.
        if (remainingTasks.Count == 0 && _roomReservations != null)
        {
            var confirmation = await _roomReservations.TryConfirmForRevisionAsync(revision.Id, ct);
            if (!confirmation.Confirmed)
            {
                // The room service clears tracking after an exclusion violation.
                _db.ChangeTracker.Clear();
                letterRequest = await _db.LetterRequests.SingleAsync(x => x.Id == revision.RequestId, ct);
                letterRequest.MarkAwaitingResourceResolution();
                _db.AuditLogs.Add(AuditLog.Record(actorUserId, "letter.resource_conflict", "LetterRequest",
                    letterRequest.Id, revision.Id, now, Guid.NewGuid().ToString("N"),
                    "Keputusan final ditunda karena jadwal fasilitas bentrok dengan reservasi terkonfirmasi lain.",
                    ipAddress, userAgent));
                await _db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                throw new SignItDomainException(DomainErrorKind.Conflict, "resource_schedule_conflict",
                    "Jadwal fasilitas bentrok. Tugas tetap aktif dan dapat dicoba kembali setelah konflik selesai.");
            }
        }

        var actor = await _db.Users.AsNoTracking().SingleAsync(x => x.Id == actorUserId, ct);
        SignatureEvidence? evidence = null;
        if (expectedActionType is WorkflowActionType.Sign or WorkflowActionType.ApproveAndSign)
        {
            var actorQr = await _qrService.EnsureActiveEntityAsync(actorUserId, ct);
            var principal = delegation == null
                ? null
                : await _db.Users.AsNoTracking().SingleAsync(x => x.Id == currentTask.AssignedUserId, ct);
            evidence = SignatureEvidence.Create(Guid.NewGuid(), currentTask.Id, revision.Id, actorUserId,
                participant?.Role.ToString() ?? currentTask.ActionType.ToString(), participant?.PositionSnapshot,
                revision.ContentHash, actorQr.Id, actorQr.Version, actorQr.ImageSha256, now,
                delegation?.FromUserId, principal == null ? null : $"a.n. {principal.Name}", ipAddress, userAgent);
            _db.SignatureEvidences.Add(evidence);
            _db.SigningAttempts.Add(SigningAttempt.Create(Guid.NewGuid(), currentTask.Id, revision.Id,
                actorUserId, actorQr.Id, actorQr.Version, actorQr.ImageSha256, revision.ContentHash,
                "Success", idempotencyKey, now));
        }

        var status = expectedActionType switch
        {
            WorkflowActionType.Sign => WorkflowTaskStatus.Signed,
            WorkflowActionType.ApproveAndSign => WorkflowTaskStatus.Approved,
            WorkflowActionType.Acknowledge => WorkflowTaskStatus.Acknowledged,
            _ => throw new InvalidOperationException("Aksi workflow tidak dapat diselesaikan.")
        };
        currentTask.Complete(status, actorUserId, now, request.Comment);
        _db.AuditLogs.Add(AuditLog.Record(actorUserId, $"workflow.{status.ToString().ToLowerInvariant()}",
            "WorkflowTask", currentTask.Id, revision.Id, now, Guid.NewGuid().ToString("N"),
            $"Actor {actor.Id}; mandat {delegation?.Id}", ipAddress, userAgent));

        var allTasks = await _db.WorkflowTasks
            .Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync(ct);
        var next = allTasks.FirstOrDefault(x => !WorkflowTaskAccess.IsDone(x.Status));
        if (next?.Status == WorkflowTaskStatus.Pending)
        {
            next.Activate(now);
            _db.AuditLogs.Add(AuditLog.Record(null, "task.activated", "WorkflowTask", next.Id,
                revision.Id, now, Guid.NewGuid().ToString("N"),
                $"Tugas berikutnya diaktifkan: Order {next.Order} untuk user {next.AssignedUserId}"));
        }

        // The finalizer must query the just-written evidence.
        await _db.SaveChangesAsync(ct);
        string? verificationCode = null;
        if (next == null)
            verificationCode = await FinalizeDocumentInternalAsync(letterRequest, revision, ct);

        var result = new SignTaskResultDto(currentTask.Id, currentTask.Status, now,
            evidence?.Id ?? Guid.Empty, evidence?.Role ?? "Acknowledgement", evidence?.PositionSnapshot,
            revision.ContentHash, evidence?.QrHash ?? string.Empty,
            letterRequest.Status == LetterStatus.Completed, verificationCode);
        WorkflowIdempotency.Record(_db, actorUserId, taskId, revision.Id, operation, correlation, request, result, now);
        await _db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<bool> RetryFinalizationAsync(Guid requestId, CancellationToken ct)
    {
        var letterRequest = await _db.LetterRequests.SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (letterRequest == null || letterRequest.Status is not
            (LetterStatus.ProcessingFailed or LetterStatus.Finalizing or LetterStatus.AwaitingResourceResolution))
            return false;
        var revision = await _db.LetterRevisions.SingleOrDefaultAsync(x => x.Id == letterRequest.CurrentRevisionId, ct);
        if (revision == null) return false;
        if (letterRequest.Status == LetterStatus.AwaitingResourceResolution && _roomReservations != null
            && !(await _roomReservations.TryConfirmForRevisionAsync(revision.Id, ct)).Confirmed)
            return false;
        await FinalizeDocumentInternalAsync(letterRequest, revision, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string> FinalizeDocumentInternalAsync(LetterRequest letterRequest, LetterRevision revision, CancellationToken ct)
    {
        letterRequest.MarkFinalizing();
        var now = _clock.GetUtcNow();
        try
        {
            byte[]? basePdfBytes = null;
            if (revision.ReviewDocumentId.HasValue)
            {
                var review = await _db.Documents.SingleOrDefaultAsync(x => x.Id == revision.ReviewDocumentId.Value, ct);
                if (review != null) basePdfBytes = await _storage.ReadBytesAsync(review.StorageKey, ct);
            }
            if (basePdfBytes == null || basePdfBytes.Length == 0)
                throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "review_document_missing",
                    "Dokumen yang telah ditinjau tidak tersedia.");

            var participants = await _db.LetterParticipants.Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
            var evidences = await _db.SignatureEvidences.Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
            var overlays = new List<PdfSignatureOverlayItem>();
            foreach (var participant in participants)
            {
                var task = await _db.WorkflowTasks.SingleOrDefaultAsync(
                    x => x.RevisionId == revision.Id && x.ParticipantId == participant.Id, ct);
                var evidence = task == null ? null : evidences.SingleOrDefault(x => x.TaskId == task.Id);
                if (evidence == null)
                    throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "signature_evidence_missing",
                        "Bukti tanda tangan peserta belum lengkap.");
                var qr = await _db.SignatureQrs.SingleOrDefaultAsync(x => x.Id == evidence.QrAssetId, ct);
                var qrBytes = qr == null ? null : await _storage.ReadBytesAsync(qr.PrivateStorageKey, ct);
                if (qrBytes == null || !string.Equals(_qrGenerator.ComputeSha256(qrBytes), evidence.QrHash, StringComparison.OrdinalIgnoreCase))
                    throw new SignItDomainException(DomainErrorKind.ProcessingFailed, "signature_asset_invalid",
                        "Snapshot aset tanda tangan tidak tersedia atau telah berubah.");
                overlays.Add(new PdfSignatureOverlayItem(participant.PageIndex, participant.X, participant.Y,
                    participant.Width, participant.Height, participant.Rotation, qrBytes,
                    participant.DisplayNameSnapshot, participant.PositionSnapshot, evidence.SignedAt, true));
            }

            var verificationCode = $"SIG-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
            var finalPdf = await _pdfOverlay.OverlaySignaturesAsync(basePdfBytes, overlays, verificationCode, ct);
            var finalHash = Convert.ToHexStringLower(SHA256.HashData(finalPdf));
            var storageKey = $"documents/{letterRequest.Id}/{revision.Id}/final.pdf";
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
            _logger.LogInformation("Letter {LetterId} finalized successfully. Code: {VerificationCode}",
                letterRequest.Id, verificationCode);
            return verificationCode;
        }
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
