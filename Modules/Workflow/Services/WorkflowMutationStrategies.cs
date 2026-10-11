using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

internal static class WorkflowMutationHelper
{
    public static async Task RevokeMandatesAsync(AppDbContext db, Guid revisionId, CancellationToken ct)
    {
        var taskIds = await db.WorkflowTasks.Where(x => x.RevisionId == revisionId).Select(x => x.Id).ToListAsync(ct);
        var scopes = taskIds.Select(WorkflowTaskAccess.DelegationScope).ToArray();
        var mandates = await db.Delegations
            .Where(x => x.Scope != null && scopes.Contains(x.Scope) && x.IsActive)
            .ToListAsync(ct);
        foreach (var mandate in mandates)
        {
            mandate.Revoke();
        }
    }
}

public sealed class RejectMutationStrategy(AppDbContext db, WorkflowEmailService emails) : IWorkflowMutationStrategy
{
    public string Action => "reject";

    public async Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        context.Task.Reject(actor, now, request.Reason);
        context.Letter.MarkRejected();

        var pending = await db.WorkflowTasks
            .Where(x => x.RevisionId == context.Task.RevisionId && x.Id != context.Task.Id)
            .ToListAsync(ct);
        foreach (var other in pending)
        {
            other.Cancel();
        }

        await WorkflowMutationHelper.RevokeMandatesAsync(db, context.Task.RevisionId, ct);
        await emails.CancelQueuedRemindersAsync(context.Task.Id, ct);

        var submitter = await db.Users.AsNoTracking().SingleAsync(x => x.Id == context.Letter.SubmittedByUserId, ct);
        await emails.EnqueueRejectedAsync(context.Letter, submitter, request.Reason, ct);

        return null;
    }
}

public sealed class RequestRevisionMutationStrategy(AppDbContext db, WorkflowEmailService emails) : IWorkflowMutationStrategy
{
    public string Action => "request-revision";

    public async Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        context.Task.RequestRevision(actor, now, request.Reason);
        context.Letter.MarkNeedsRevision();

        var pending = await db.WorkflowTasks
            .Where(x => x.RevisionId == context.Task.RevisionId && x.Id != context.Task.Id)
            .ToListAsync(ct);
        foreach (var other in pending)
        {
            other.Cancel();
        }

        await WorkflowMutationHelper.RevokeMandatesAsync(db, context.Task.RevisionId, ct);
        await emails.CancelQueuedRemindersAsync(context.Task.Id, ct);

        var submitter = await db.Users.AsNoTracking().SingleAsync(x => x.Id == context.Letter.SubmittedByUserId, ct);
        await emails.EnqueueRevisionRequestedAsync(context.Letter, context.Task.RevisionId, submitter, request.Reason, ct);

        return null;
    }
}

public sealed class DeferMutationStrategy(WorkflowEmailService emails) : IWorkflowMutationStrategy
{
    public string Action => "defer";

    public async Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (request.Until is null || request.Until <= now || request.Until > now.AddDays(30))
        {
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_defer_until", "Batas penundaan harus di masa depan, maksimal 30 hari.");
        }

        context.Task.Defer(actor, now, request.Until.Value, request.Reason);
        await emails.CancelQueuedRemindersAsync(context.Task.Id, ct);

        return null;
    }
}

public sealed class ResumeMutationStrategy : IWorkflowMutationStrategy
{
    public string Action => "resume";

    public Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        context.Task.Activate(now);
        return Task.FromResult<Guid?>(null);
    }
}

public sealed class DelegateMutationStrategy(AppDbContext db, WorkflowTaskAccess access) : IWorkflowMutationStrategy
{
    public string Action => "delegate";

    public async Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (request.DelegateUserId is null || request.DelegateUserId == actor || request.Until is null
            || request.Until <= now || request.Until > now.AddDays(30)
            || !await access.IsEligibleAsync(context, request.DelegateUserId.Value, now, ct)
            || !await access.IsEligibleAsync(context, request.DelegateUserId.Value, request.Until.Value.AddTicks(-1), ct))
        {
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_delegate", "Pengganti wajib memiliki assignment jabatan/organisasi sesuai dan masa berlaku mandat maksimal 30 hari.");
        }

        var scope = WorkflowTaskAccess.DelegationScope(context.Task.Id);
        var existing = await db.Delegations.Where(x => x.Scope == scope && x.IsActive).ToListAsync(ct);
        foreach (var mandate in existing)
        {
            mandate.Revoke();
        }

        var delegation = Delegation.Create(Guid.NewGuid(), actor, request.DelegateUserId.Value, scope, now, request.Until.Value, request.Reason);
        db.Delegations.Add(delegation);
        context.Task.Touch();

        return delegation.Id;
    }
}

public sealed class RevokeDelegationMutationStrategy(AppDbContext db) : IWorkflowMutationStrategy
{
    public string Action => "revoke-delegation";

    public async Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var mandates = await db.Delegations
            .Where(x => x.Scope == WorkflowTaskAccess.DelegationScope(context.Task.Id) && x.IsActive)
            .ToListAsync(ct);
        foreach (var mandate in mandates)
        {
            mandate.Revoke();
        }

        context.Task.Touch();
        return null;
    }
}
