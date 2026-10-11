using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public sealed class WorkflowTaskDtoMapper : IWorkflowTaskDtoMapper
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public WorkflowTaskDtoMapper(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<WorkflowTaskDto> MapAsync(TaskContext context, Guid actor, bool canAct, CancellationToken ct)
    {
        var task = context.Task;

        var assignedUserName = await _db.Users.AsNoTracking()
            .Where(x => x.Id == task.AssignedUserId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(ct) ?? string.Empty;

        var requesterName = await _db.Users.AsNoTracking()
            .Where(x => x.Id == context.Letter.SubmittedByUserId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(ct) ?? string.Empty;

        var organizationName = context.Letter.OrganizationId is Guid organizationId
            ? await _db.Organizations.AsNoTracking()
                .Where(x => x.Id == organizationId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;

        string? positionName = null;
        if (!string.IsNullOrEmpty(task.DomainCode) && context.Letter.OrganizationId is Guid letterOrganizationId)
        {
            positionName = await (from assignment in _db.Assignments.AsNoTracking()
                                  join organization in _db.Organizations.AsNoTracking() on assignment.Scope equals organization.Scope
                                  where organization.Id == letterOrganizationId && assignment.UserId == task.AssignedUserId
                                    && assignment.PositionCode == task.DomainCode && assignment.IsActive
                                  select assignment.PositionName).FirstOrDefaultAsync(ct);
        }

        var actions = await EvaluateAllowedActionsAsync(context, actor, canAct, ct);

        return new WorkflowTaskDto(
            task.Id,
            context.Letter.Id,
            context.Letter.Title,
            task.RevisionId,
            context.Revision.ContentHash,
            task.Order,
            task.ActionType,
            task.Status,
            task.AssignedUserId,
            task.RowVersion,
            task.ActivatedAt,
            task.DueAt,
            task.Status is WorkflowTaskStatus.Active or WorkflowTaskStatus.Deferred && task.DueAt < _clock.GetUtcNow(),
            task.Comment,
            task.ActedByUserId,
            task.ActedAt,
            actions,
            context.Revision.ReviewDocumentId.HasValue ? $"/api/v1/tasks/{task.Id}/document" : null,
            assignedUserName,
            task.DomainCode,
            positionName,
            context.Letter.Number,
            context.Letter.TypeId,
            requesterName,
            organizationName);
    }

    private async Task<string[]> EvaluateAllowedActionsAsync(TaskContext context, Guid actor, bool canAct, CancellationToken ct)
    {
        var task = context.Task;
        var actions = new List<string>();

        var precedingIncomplete = await _db.WorkflowTasks.AnyAsync(x => x.RevisionId == task.RevisionId && x.Order < task.Order
            && x.Status != WorkflowTaskStatus.Signed && x.Status != WorkflowTaskStatus.Approved && x.Status != WorkflowTaskStatus.Acknowledged, ct);

        if (canAct && !precedingIncomplete
            && context.Letter.Status is (LetterStatus.InProgress or LetterStatus.AwaitingResourceResolution)
            && context.Letter.CurrentRevisionId == task.RevisionId)
        {
            if (task.Status == WorkflowTaskStatus.Deferred)
            {
                actions.Add("resume");
            }

            if (task.Status == WorkflowTaskStatus.Active)
            {
                if (task.ActionType == WorkflowActionType.Sign) actions.Add("sign");
                if (task.ActionType == WorkflowActionType.ApproveAndSign) actions.Add("approve");
                if (task.ActionType == WorkflowActionType.Acknowledge && task.ParticipantId == null) actions.Add("acknowledge");
                actions.AddRange(["reject", "request-revision", "defer"]);
                if (actor == task.AssignedUserId) actions.AddRange(["delegate", "revoke-delegation"]);
            }
        }

        return actions.ToArray();
    }
}
