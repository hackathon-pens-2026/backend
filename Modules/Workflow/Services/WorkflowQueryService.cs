using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public sealed record WorkflowTaskDto(Guid Id, Guid LetterId, string Title, Guid RevisionId, string ContentHash,
    int Order, WorkflowActionType ActionType, WorkflowTaskStatus Status, Guid AssignedUserId,
    Guid Version, DateTimeOffset? ActivatedAt, DateTimeOffset? DueAt, bool IsOverdue, string? Comment,
    Guid? ActedByUserId, DateTimeOffset? ActedAt, string[] AllowedActions, string? DocumentUrl,
    string AssignedUserName, string? PositionCode, string? PositionName,
    string Number, string TypeId, string RequesterName, string OrganizationName);
public sealed record WorkflowQueueDto(int Page, int PageSize, int Total, WorkflowTaskDto[] Items);
public sealed record WorkflowTimelineDto(string Action, Guid? Actor, DateTimeOffset At, string? Reason);
public sealed record DelegateCandidateDto(Guid UserId, string Name, string PositionName, DateTimeOffset? ValidTo);
public sealed record LetterWorkflowDto(Guid LetterId, string Number, LetterStatus Status, Guid Version,
    Guid RevisionId, string ContentHash, Guid? FinalDocumentId, WorkflowTaskDto[] Tasks, WorkflowTimelineDto[] Timeline);

public sealed class WorkflowQueryService(AppDbContext db, WorkflowTaskAccess access, IStorageService storage, TimeProvider clock)
{
    public async Task<DelegateCandidateDto[]> CandidatesAsync(Guid actor, Guid taskId, CancellationToken ct)
    {
        var context = await access.LoadAsync(taskId, false, ct);
        await access.AuthorizeAsync(context, actor, ct);
        if (actor != context.Task.AssignedUserId)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Forbidden, "delegation_depth_exceeded", "Hanya pemilik asli dapat memilih pengganti.");
        var now = clock.GetUtcNow();
        var organization = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == context.Letter.OrganizationId, ct);
        var capability = context.Task.ActionType == WorkflowActionType.Sign ? UserCapability.Signer : UserCapability.Approver;
        return await (from assignment in db.Assignments.AsNoTracking()
                      join user in db.Users on assignment.UserId equals user.Id
                      where user.IsActive && user.Id != actor && assignment.IsActive && assignment.Scope == organization.Scope
                        && assignment.PositionCode == context.Task.DomainCode && assignment.Capability == capability
                        && assignment.ValidFrom <= now && (assignment.ValidTo == null || assignment.ValidTo > now)
                        && (context.Task.ActionType != WorkflowActionType.ApproveAndSign || user.Id != context.Letter.SubmittedByUserId)
                      select new DelegateCandidateDto(user.Id, user.Name, assignment.PositionName, assignment.ValidTo))
            .Distinct().OrderBy(x => x.Name).ThenBy(x => x.UserId).ThenBy(x => x.ValidTo).Take(100).ToArrayAsync(ct);
    }

    private IQueryable<WorkflowTask> Authorized(Guid actor)
    {
        var now = clock.GetUtcNow();
        return from task in db.WorkflowTasks.AsNoTracking()
               join revision in db.LetterRevisions on task.RevisionId equals revision.Id
               join letter in db.LetterRequests on revision.RequestId equals letter.Id
               join organization in db.Organizations on letter.OrganizationId equals organization.Id
               where organization.IsActive && db.Users.Any(x => x.Id == actor && x.IsActive)
                 && db.Users.Any(x => x.Id == task.AssignedUserId && x.IsActive)
                 && db.Assignments.Any(x => x.UserId == task.AssignedUserId && x.Scope == organization.Scope && x.PositionCode == task.DomainCode
                     && x.Capability == (task.ActionType == WorkflowActionType.Sign ? UserCapability.Signer : UserCapability.Approver)
                     && x.IsActive && x.ValidFrom <= now && (x.ValidTo == null || x.ValidTo > now))
                 && (task.ActionType != WorkflowActionType.ApproveAndSign || letter.SubmittedByUserId != actor)
                 && db.Assignments.Any(x => x.UserId == actor && x.Scope == organization.Scope && x.PositionCode == task.DomainCode
                     && x.Capability == (task.ActionType == WorkflowActionType.Sign ? UserCapability.Signer : UserCapability.Approver)
                     && x.IsActive && x.ValidFrom <= now && (x.ValidTo == null || x.ValidTo > now))
                 && (task.AssignedUserId == actor || db.Delegations.Any(x => x.FromUserId == task.AssignedUserId && x.ToUserId == actor
                     && x.Scope == "task:" + task.Id.ToString().Replace("-", "") && x.IsActive && x.StartAt <= now && x.EndAt > now))
               select task;
    }

    public async Task<WorkflowQueueDto> QueueAsync(Guid actor, int page, int pageSize, CancellationToken ct)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "invalid_pagination", "Page mulai 1; pageSize maksimal 100.");
        var query = Authorized(actor).Where(task => task.Status == WorkflowTaskStatus.Active
            && db.LetterRequests.Any(letter => letter.CurrentRevisionId == task.RevisionId && letter.Status == LetterStatus.InProgress));
        var total = await query.CountAsync(ct);
        var tasks = await query.OrderBy(x => x.ActivatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<WorkflowTaskDto>();
        foreach (var task in tasks) items.Add(await MapAsync(await access.LoadAsync(task.Id, false, ct), actor, true, ct));
        return new(page, pageSize, total, items.ToArray());
    }

    public async Task<WorkflowTaskDto> GetAsync(Guid actor, Guid taskId, CancellationToken ct)
    {
        var context = await access.LoadAsync(taskId, false, ct);
        var canAct = await Authorized(actor).AnyAsync(x => x.Id == taskId, ct);
        if (context.Letter.SubmittedByUserId != actor && !canAct)
            throw WorkflowTaskAccess.Error(DomainErrorKind.NotFound, "task_not_found", "Tugas tidak ditemukan.");
        return await MapAsync(context, actor, canAct, ct);
    }

    public async Task<LetterWorkflowDto> LetterAsync(Guid actor, Guid letterId, CancellationToken ct)
    {
        var letter = await db.LetterRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == letterId, ct)
            ?? throw WorkflowTaskAccess.Error(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        var authorizedIds = await Authorized(actor).Where(x => db.LetterRevisions.Any(r => r.Id == x.RevisionId && r.RequestId == letterId))
            .Select(x => x.Id).ToListAsync(ct);
        if (letter.SubmittedByUserId != actor && authorizedIds.Count == 0)
            throw WorkflowTaskAccess.Error(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        var revision = await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        var tasks = await db.WorkflowTasks.AsNoTracking().Where(x => x.RevisionId == revision.Id).OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync(ct);
        var items = new List<WorkflowTaskDto>();
        foreach (var task in tasks) items.Add(await MapAsync(new(task, revision, letter), actor, authorizedIds.Contains(task.Id), ct));
        var revisionIds = db.LetterRevisions.Where(x => x.RequestId == letterId).Select(x => x.Id);
        var timeline = await db.AuditLogs.AsNoTracking().Where(x => x.RevisionId.HasValue && revisionIds.Contains(x.RevisionId.Value)
            && (x.Action.StartsWith("workflow.") || x.Action == "task.activated" || x.Action == "letter.submitted" || x.Action == "letter.cancelled" || x.Action == "letter.edited"))
            .OrderByDescending(x => x.AtUtc).ThenByDescending(x => x.Id).Take(100)
            .Select(x => new WorkflowTimelineDto(x.Action, x.ActorUserId, x.AtUtc, x.Action.StartsWith("workflow.") ? x.Details : null)).ToArrayAsync(ct);
        return new(letter.Id, letter.Number, letter.Status, letter.RowVersion, revision.Id, revision.ContentHash,
            revision.FinalDocumentId, items.ToArray(), timeline);
    }

    public async Task<byte[]> DownloadAsync(Guid actor, Guid taskId, CancellationToken ct)
    {
        await GetAsync(actor, taskId, ct);
        var context = await access.LoadAsync(taskId, false, ct);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == context.Revision.ReviewDocumentId && x.Kind == DocumentKind.Review && x.ProcessingState == "Ready", ct)
            ?? throw WorkflowTaskAccess.Error(DomainErrorKind.NotFound, "document_not_found", "Dokumen tinjauan tidak tersedia.");
        var bytes = await storage.ReadBytesAsync(document.StorageKey, ct);
        if (bytes == null || LetterPreviewService.Hash(bytes) != document.Sha256)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Conflict, "review_unavailable", "Integritas dokumen tidak sesuai.");
        return bytes;
    }

    private async Task<WorkflowTaskDto> MapAsync(TaskContext context, Guid actor, bool canAct, CancellationToken ct)
    {
        var task = context.Task;
        var assignedUserName = await db.Users.AsNoTracking().Where(x => x.Id == task.AssignedUserId)
            .Select(x => x.Name).SingleOrDefaultAsync(ct) ?? string.Empty;
        var requesterName = await db.Users.AsNoTracking().Where(x => x.Id == context.Letter.SubmittedByUserId)
            .Select(x => x.Name).SingleOrDefaultAsync(ct) ?? string.Empty;
        var organizationName = context.Letter.OrganizationId is Guid organizationId
            ? await db.Organizations.AsNoTracking().Where(x => x.Id == organizationId)
                .Select(x => x.Name).SingleOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        string? positionName = null;
        if (!string.IsNullOrEmpty(task.DomainCode) && context.Letter.OrganizationId is Guid letterOrganizationId)
            positionName = await (from assignment in db.Assignments.AsNoTracking()
                                  join organization in db.Organizations.AsNoTracking() on assignment.Scope equals organization.Scope
                                  where organization.Id == letterOrganizationId && assignment.UserId == task.AssignedUserId
                                    && assignment.PositionCode == task.DomainCode && assignment.IsActive
                                  select assignment.PositionName).FirstOrDefaultAsync(ct);
        var actions = new List<string>();
        var precedingIncomplete = await db.WorkflowTasks.AnyAsync(x => x.RevisionId == task.RevisionId && x.Order < task.Order
            && x.Status != WorkflowTaskStatus.Signed && x.Status != WorkflowTaskStatus.Approved && x.Status != WorkflowTaskStatus.Acknowledged, ct);
        if (canAct && !precedingIncomplete && context.Letter.Status == LetterStatus.InProgress && context.Letter.CurrentRevisionId == task.RevisionId)
        {
            if (task.Status == WorkflowTaskStatus.Deferred) actions.Add("resume");
            if (task.Status == WorkflowTaskStatus.Active)
            {
                if (task.ActionType == WorkflowActionType.Sign) actions.Add("sign");
                if (task.ActionType == WorkflowActionType.ApproveAndSign) actions.Add("approve");
                if (task.ActionType == WorkflowActionType.Acknowledge && task.ParticipantId == null) actions.Add("acknowledge");
                actions.AddRange(["reject", "request-revision", "defer"]);
                if (actor == task.AssignedUserId) actions.AddRange(["delegate", "revoke-delegation"]);
            }
        }
        return new(task.Id, context.Letter.Id, context.Letter.Title, task.RevisionId, context.Revision.ContentHash, task.Order,
            task.ActionType, task.Status, task.AssignedUserId, task.RowVersion, task.ActivatedAt, task.DueAt,
            task.Status is WorkflowTaskStatus.Active or WorkflowTaskStatus.Deferred && task.DueAt < clock.GetUtcNow(),
            task.Comment, task.ActedByUserId, task.ActedAt, actions.ToArray(),
            context.Revision.ReviewDocumentId.HasValue ? $"/api/v1/tasks/{task.Id}/document" : null,
            assignedUserName, task.DomainCode, positionName,
            context.Letter.Number, context.Letter.TypeId, requesterName, organizationName);
    }
}
