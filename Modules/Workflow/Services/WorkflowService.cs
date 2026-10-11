using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public sealed record WorkflowMutationRequest(
    [property: JsonRequired] Guid ExpectedRevisionId, [property: JsonRequired] string ExpectedContentHash,
    [property: JsonRequired] Guid ExpectedTaskVersion, [property: JsonRequired] string Reason,
    Guid? DelegateUserId = null, DateTimeOffset? Until = null);
public sealed record WorkflowMutationDto(Guid TaskId, WorkflowTaskStatus Status, Guid TaskVersion, LetterStatus LetterStatus, Guid LetterVersion, Guid? DelegationId = null);

public sealed class WorkflowService
{
    private readonly AppDbContext _db;
    private readonly WorkflowTaskAccess _access;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, IWorkflowMutationStrategy> _strategies;

    public WorkflowService(
        AppDbContext db,
        WorkflowTaskAccess access,
        WorkflowEmailService emails,
        TimeProvider clock,
        IEnumerable<IWorkflowMutationStrategy>? strategies = null)
    {
        _db = db;
        _access = access;
        _clock = clock;
        _strategies = (strategies ?? CreateDefaultStrategies(db, access, emails))
            .ToDictionary(s => s.Action, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<IWorkflowMutationStrategy> CreateDefaultStrategies(
        AppDbContext db, WorkflowTaskAccess access, WorkflowEmailService emails)
    {
        yield return new RejectMutationStrategy(db, emails);
        yield return new RequestRevisionMutationStrategy(db, emails);
        yield return new DeferMutationStrategy(emails);
        yield return new ResumeMutationStrategy();
        yield return new DelegateMutationStrategy(db, access);
        yield return new RevokeDelegationMutationStrategy(db);
    }

    public async Task<WorkflowMutationDto> MutateAsync(Guid actor, Guid taskId, string action,
        WorkflowMutationRequest request, string key, CancellationToken ct)
    {
        if (!_strategies.TryGetValue(action, out var strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        var now = _clock.GetUtcNow();
        var operation = $"task.{action}";
        var correlation = WorkflowIdempotency.Correlation(actor, taskId, operation, key);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > (action == "delegate" ? 500 : 1000)
            || request.Reason.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw WorkflowTaskAccess.Error(DomainErrorKind.Validation, "reason_required", "Alasan wajib dan harus sesuai batas panjang.");
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
        var context = await _access.LoadAsync(taskId, true, ct);
        await _access.AuthorizeAsync(context, actor, ct);
        if (action is "delegate" or "revoke-delegation" && actor != context.Task.AssignedUserId)
            throw WorkflowTaskAccess.Error(DomainErrorKind.Forbidden, "delegation_depth_exceeded", "Hanya pemilik asli tugas dapat memberi/mencabut mandat; tidak ada delegasi berantai.");
        var replay = await WorkflowIdempotency.ReplayAsync<WorkflowMutationDto>(_db, taskId, operation, correlation, request, ct);
        if (replay != null) { if (transaction != null) await transaction.CommitAsync(ct); return replay; }
        await _access.ValidateAsync(context, request.ExpectedRevisionId, request.ExpectedContentHash, request.ExpectedTaskVersion,
            action == "resume" || action == "revoke-delegation" && context.Task.Status == WorkflowTaskStatus.Deferred
                ? WorkflowTaskStatus.Deferred : WorkflowTaskStatus.Active, ct);

        var task = context.Task;
        var delegationId = await strategy.ExecuteAsync(context, actor, request, now, ct);

        var result = new WorkflowMutationDto(task.Id, task.Status, task.RowVersion, context.Letter.Status, context.Letter.RowVersion, delegationId);
        WorkflowIdempotency.Record(_db, actor, taskId, task.RevisionId, operation, correlation, request, result, now);
        // Store the readable reason separately, not in the replay fingerprint.
        _db.AuditLogs.Add(AuditLog.Record(actor, $"workflow.{action}", "WorkflowTask", taskId, task.RevisionId, now, Guid.NewGuid().ToString("N"), request.Reason));
        await _db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return result;
    }
}
