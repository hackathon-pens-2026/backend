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
    private readonly ILetterDocumentFinalizer _finalizer;

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
        RoomReservationService? roomReservations = null,
        ILetterDocumentFinalizer? finalizer = null)
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
        _finalizer = finalizer ?? new LetterDocumentFinalizer(
            db, storage, qrGenerator, pdfOverlay, emails, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LetterDocumentFinalizer>.Instance);
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
            .Where(x => x.Id != currentTask.Id && !x.IsDone).ToList();

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

        currentTask.CompleteWithAction(expectedActionType, actorUserId, now, request.Comment);
        var status = currentTask.Status;
        _db.AuditLogs.Add(AuditLog.Record(actorUserId, $"workflow.{status.ToString().ToLowerInvariant()}",
            "WorkflowTask", currentTask.Id, revision.Id, now, Guid.NewGuid().ToString("N"),
            $"Actor {actor.Id}; mandat {delegation?.Id}", ipAddress, userAgent));
        // Tugas selesai: hentikan reminder yang masih menunggu.
        await _emails.CancelQueuedRemindersAsync(currentTask.Id, ct);

        var allTasks = await _db.WorkflowTasks
            .Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync(ct);
        var next = allTasks.FirstOrDefault(x => !x.IsDone);
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
        await _finalizer.FinalizeDocumentAsync(letterRequest, revision, ct);
        await _db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return letterRequest.Status == LetterStatus.Completed;
    }
}
