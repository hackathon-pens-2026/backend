using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public sealed record TaskContext(WorkflowTask Task, LetterRevision Revision, LetterRequest Letter);

public sealed class WorkflowTaskAccess(AppDbContext db, TimeProvider clock)
{
    public static string DelegationScope(Guid taskId) => $"task:{taskId:N}";
    public static bool IsDone(WorkflowTaskStatus status) => status is WorkflowTaskStatus.Signed or WorkflowTaskStatus.Approved or WorkflowTaskStatus.Acknowledged;

    // All task and letter writers acquire the letter lock first. Reload prevents tracked stale rows after waiting.
    public async Task<TaskContext> LoadAsync(Guid taskId, bool forUpdate, CancellationToken ct)
    {
        var task = await db.WorkflowTasks.SingleOrDefaultAsync(x => x.Id == taskId, ct)
            ?? throw Error(DomainErrorKind.NotFound, "task_not_found", "Tugas tidak ditemukan.");
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId, ct);
        var letter = forUpdate && db.Database.IsRelational()
            ? await db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={revision.RequestId} FOR UPDATE").SingleAsync(ct)
            : await db.LetterRequests.SingleAsync(x => x.Id == revision.RequestId, ct);
        if (forUpdate)
        {
            await db.Entry(letter).ReloadAsync(ct);
            await db.Entry(task).ReloadAsync(ct);
        }
        return new(task, revision, letter);
    }

    public async Task<Delegation?> AuthorizeAsync(TaskContext context, Guid actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!await IsEligibleAsync(context, actor, now, ct))
            throw Error(DomainErrorKind.Forbidden, "task_scope_denied", "Assignment untuk tugas ini tidak aktif atau tidak sesuai lingkup.");
        if (context.Task.AssignedUserId == actor) return null;
        if (!await IsEligibleAsync(context, context.Task.AssignedUserId, now, ct))
            throw Error(DomainErrorKind.Forbidden, "delegation_principal_inactive", "Mandat tidak dapat digunakan karena assignment pemberi mandat tidak aktif.");
        var scope = DelegationScope(context.Task.Id);
        var delegation = await db.Delegations.SingleOrDefaultAsync(x => x.FromUserId == context.Task.AssignedUserId
            && x.ToUserId == actor && x.Scope == scope && x.IsActive && x.StartAt <= now && x.EndAt > now, ct);
        if (delegation == null)
            throw Error(DomainErrorKind.Forbidden, "forbidden_task_actor", "Anda bukan pemilik tugas atau penerima mandat aktif untuk tugas ini.");
        return delegation;
    }

    public async Task<bool> IsEligibleAsync(TaskContext context, Guid actor, DateTimeOffset at, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.Letter.OrganizationId && x.IsActive, ct);
        if (organization == null || !await db.Users.AnyAsync(x => x.Id == actor && x.IsActive, ct)) return false;
        if (actor == context.Letter.SubmittedByUserId && context.Task.ActionType == WorkflowActionType.ApproveAndSign) return false;
        var capability = context.Task.ActionType == WorkflowActionType.Sign ? UserCapability.Signer : UserCapability.Approver;
        return await db.Assignments.AnyAsync(x => x.UserId == actor && x.IsActive && x.Scope == organization.Scope
            && x.PositionCode == context.Task.DomainCode && x.Capability == capability && x.ValidFrom <= at
            && (x.ValidTo == null || x.ValidTo > at), ct);
    }

    public async Task ValidateAsync(TaskContext context, Guid revisionId, string contentHash, Guid version,
        WorkflowTaskStatus expectedStatus, CancellationToken ct)
    {
        if (context.Revision.Id != revisionId || !string.Equals(context.Revision.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
            throw Error(DomainErrorKind.Conflict, "revision_hash_mismatch", "Revisi/isi berubah. Tinjau ulang dokumen.");
        if (context.Letter.CurrentRevisionId != revisionId
            || context.Letter.Status is not (LetterStatus.InProgress or LetterStatus.AwaitingResourceResolution))
            throw Error(DomainErrorKind.Conflict, "inactive_letter_revision", "Revisi ini bukan pengajuan aktif.");
        if (context.Task.Status != expectedStatus)
            throw Error(DomainErrorKind.Conflict, "task_not_active", "Tugas tidak berada dalam status yang dapat ditindak.");
        if (version == Guid.Empty || context.Task.RowVersion != version)
            throw Error(DomainErrorKind.Conflict, "stale_task", "Tugas atau mandat berubah. Muat ulang tugas.");
        if (await db.WorkflowTasks.AnyAsync(x => x.RevisionId == revisionId && x.Id != context.Task.Id && x.Status == WorkflowTaskStatus.Active, ct))
            throw Error(DomainErrorKind.Conflict, "workflow_multiple_active_tasks", "Workflow memiliki lebih dari satu giliran aktif. Hubungi tim untuk pemeriksaan data.");
        if (await db.WorkflowTasks.AnyAsync(x => x.RevisionId == revisionId && x.Order < context.Task.Order
            && x.Status != WorkflowTaskStatus.Signed && x.Status != WorkflowTaskStatus.Approved && x.Status != WorkflowTaskStatus.Acknowledged, ct))
            throw Error(DomainErrorKind.Conflict, "previous_task_incomplete", "Tahap sebelumnya belum selesai; penundaan tidak melewati tahap wajib.");
    }

    public static SignItDomainException Error(DomainErrorKind kind, string code, string message) => new(kind, code, message);
}
