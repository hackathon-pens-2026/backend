using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Rooms.Services;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;
using SignIt.Modules.Templates.Services;

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
    private readonly WorkflowEmailService _emails;
    private readonly WorkflowOptions _workflow;

    public SignatureWorkflowService(
        AppDbContext db,
        IUserSignatureQrService qrService,
        IQrCodeGenerator qrGenerator,
        IPdfOverlayService pdfOverlay,
        IStorageService storage,
        TimeProvider clock,
        ILogger<SignatureWorkflowService> logger,
        WorkflowTaskAccess access,
        WorkflowEmailService emails,
        WorkflowOptions workflow,
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
        _emails = emails;
        _workflow = workflow;
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
        // Tugas selesai: hentikan reminder yang masih menunggu.
        await _emails.CancelQueuedRemindersAsync(currentTask.Id, ct);

        var allTasks = await _db.WorkflowTasks
            .Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync(ct);
        var next = allTasks.FirstOrDefault(x => !WorkflowTaskAccess.IsDone(x.Status));
        if (next?.Status == WorkflowTaskStatus.Pending)
        {
            next.Activate(now, now.AddDays(_workflow.SlaDays));
            _db.AuditLogs.Add(AuditLog.Record(null, "task.activated", "WorkflowTask", next.Id,
                revision.Id, now, Guid.NewGuid().ToString("N"),
                $"Tugas berikutnya diaktifkan: Order {next.Order} untuk user {next.AssignedUserId}"));
            var nextUser = await _db.Users.AsNoTracking().SingleAsync(x => x.Id == next.AssignedUserId, ct);
            await _emails.EnqueueTaskActiveAsync(next, letterRequest, nextUser, ct);
        }

        // The finalizer must query the just-written evidence.
        await _db.SaveChangesAsync(ct);
        string? verificationCode = null;
        if (next == null)
        {
            // This persisted status is the durable work item. Rendering is performed
            // by the worker after the approval/evidence transaction has committed.
            letterRequest.MarkFinalizing();
            _db.AuditLogs.Add(AuditLog.Record(null, "letter.finalization_queued", "LetterRequest",
                letterRequest.Id, revision.Id, now, Guid.NewGuid().ToString("N")));
        }

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
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct) : null;
        var letterRequest = _db.Database.IsRelational()
            ? await _db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={requestId} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await _db.LetterRequests.SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (letterRequest != null) await _db.Entry(letterRequest).ReloadAsync(ct);
        if (letterRequest == null || letterRequest.Status is not
            (LetterStatus.ProcessingFailed or LetterStatus.Finalizing))
            return false;
        var revision = await _db.LetterRevisions.SingleOrDefaultAsync(x => x.Id == letterRequest.CurrentRevisionId, ct);
        if (revision == null) return false;
        var tasks = await _db.WorkflowTasks.AsNoTracking().Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
        if (tasks.Count == 0 || tasks.Any(x => !WorkflowTaskAccess.IsDone(x.Status)))
            return false;
        await FinalizeDocumentInternalAsync(letterRequest, revision, ct);
        await _db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return letterRequest.Status == LetterStatus.Completed;
    }

    private async Task<string> FinalizeDocumentInternalAsync(LetterRequest letterRequest, LetterRevision revision, CancellationToken ct)
    {
        if (letterRequest.Status != LetterStatus.Finalizing) letterRequest.MarkFinalizing();
        var now = _clock.GetUtcNow();
        try
        {
            var tasks = await _db.WorkflowTasks.AsNoTracking().Where(x => x.RevisionId == revision.Id).ToListAsync(ct);
            if (letterRequest.CurrentRevisionId != revision.Id || tasks.Count == 0
                || tasks.Any(x => !WorkflowTaskAccess.IsDone(x.Status)))
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
                    // PDF metadata IDs are not deterministic; the submitted snapshot
                    // and stored review hash are validated separately.
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
