using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public sealed record WorkflowMutationRequest(
    [property: JsonRequired] Guid ExpectedRevisionId, [property: JsonRequired] string ExpectedContentHash,
    [property: JsonRequired] Guid ExpectedTaskVersion, [property: JsonRequired] string Reason,
    Guid? DelegateUserId = null, DateTimeOffset? Until = null);
public sealed record WorkflowMutationDto(Guid TaskId, WorkflowTaskStatus Status, Guid TaskVersion, LetterStatus LetterStatus, Guid LetterVersion, Guid? DelegationId = null);

public sealed class WorkflowService(AppDbContext db, WorkflowTaskAccess access, TimeProvider clock)
{
    public async Task<WorkflowMutationDto> MutateAsync(Guid actor, Guid taskId, string action,
        WorkflowMutationRequest request, string key, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var operation = $"task.{action}";
        var correlation = WorkflowIdempotency.Correlation(actor, taskId, operation, key);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > (action == "delegate" ? 500 : 1000)
            || request.Reason.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "reason_required", "Alasan wajib dan harus sesuai batas panjang.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var context = await access.LoadAsync(taskId, true, ct);
        await access.AuthorizeAsync(context, actor, ct);
        if (action is "delegate" or "revoke-delegation" && actor != context.Task.AssignedUserId)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Forbidden, "delegation_depth_exceeded", "Hanya pemilik asli tugas dapat memberi/mencabut mandat; tidak ada delegasi berantai.");
        var replay = await WorkflowIdempotency.ReplayAsync<WorkflowMutationDto>(db, taskId, operation, correlation, request, ct);
        if (replay != null) { if (transaction != null) await transaction.CommitAsync(ct); return replay; }
        await access.ValidateAsync(context, request.ExpectedRevisionId, request.ExpectedContentHash, request.ExpectedTaskVersion,
            action == "resume" || action == "revoke-delegation" && context.Task.Status == WorkflowTaskStatus.Deferred
                ? WorkflowTaskStatus.Deferred : WorkflowTaskStatus.Active, ct);
        var task = context.Task;
        Guid? delegationId = null;
        switch (action)
        {
            case "reject":
            case "request-revision":
                if (action == "reject") { task.Reject(actor, now, request.Reason); context.Letter.MarkRejected(); }
                else { task.RequestRevision(actor, now, request.Reason); context.Letter.MarkNeedsRevision(); }
                var pending = await db.WorkflowTasks.Where(x => x.RevisionId == task.RevisionId && x.Id != taskId).ToListAsync(ct);
                foreach (var other in pending) other.Cancel();
                await RevokeMandatesAsync(task.RevisionId, ct);
                break;
            case "defer":
                if (request.Until is null || request.Until <= now || request.Until > now.AddDays(30))
                    throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_defer_until", "Batas penundaan harus di masa depan, maksimal 30 hari.");
                task.Defer(actor, now, request.Until.Value, request.Reason);
                break;
            case "resume":
                // Explicit resume only; no auto-approval or auto-skip when the deadline passes.
                task.Activate(now);
                break;
            case "delegate":
                if (request.DelegateUserId is null || request.DelegateUserId == actor || request.Until is null
                    || request.Until <= now || request.Until > now.AddDays(30)
                    || !await access.IsEligibleAsync(context, request.DelegateUserId.Value, now, ct)
                    || !await access.IsEligibleAsync(context, request.DelegateUserId.Value, request.Until.Value.AddTicks(-1), ct))
                    throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_delegate", "Pengganti wajib memiliki assignment jabatan/organisasi sesuai dan masa berlaku mandat maksimal 30 hari.");
                var scope = WorkflowTaskAccess.DelegationScope(taskId);
                var existing = await db.Delegations.Where(x => x.Scope == scope && x.IsActive).ToListAsync(ct);
                foreach (var mandate in existing) mandate.Revoke();
                var delegation = Delegation.Create(Guid.NewGuid(), actor, request.DelegateUserId.Value, scope, now, request.Until.Value, request.Reason);
                db.Delegations.Add(delegation);
                delegationId = delegation.Id;
                task.Touch();
                break;
            case "revoke-delegation":
                var mandates = await db.Delegations.Where(x => x.Scope == WorkflowTaskAccess.DelegationScope(taskId) && x.IsActive).ToListAsync(ct);
                foreach (var mandate in mandates) mandate.Revoke();
                task.Touch();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        var result = new WorkflowMutationDto(task.Id, task.Status, task.RowVersion, context.Letter.Status, context.Letter.RowVersion, delegationId);
        WorkflowIdempotency.Record(db, actor, taskId, task.RevisionId, operation, correlation, request, result, now);
        // Store the readable reason separately, not in the replay fingerprint.
        db.AuditLogs.Add(AuditLog.Record(actor, $"workflow.{action}", "WorkflowTask", taskId, task.RevisionId, now, Guid.NewGuid().ToString("N"), request.Reason));
        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return result;
    }

    private async Task RevokeMandatesAsync(Guid revisionId, CancellationToken ct)
    {
        var taskIds = await db.WorkflowTasks.Where(x => x.RevisionId == revisionId).Select(x => x.Id).ToListAsync(ct);
        var scopes = taskIds.Select(WorkflowTaskAccess.DelegationScope).ToArray();
        foreach (var mandate in await db.Delegations.Where(x => x.Scope != null && scopes.Contains(x.Scope) && x.IsActive).ToListAsync(ct)) mandate.Revoke();
    }
}
