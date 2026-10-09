using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Signatures.Services;

public interface ISignatureWorkflowService
{
    Task<SignTaskResultDto> ExecuteTaskActionAsync(
        Guid taskId,
        Guid actorUserId,
        WorkflowActionType expectedActionType,
        SignTaskRequest request,
        string? idempotencyKey,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct);

    Task RejectTaskAsync(
        Guid taskId,
        Guid actorUserId,
        string comment,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct);

    Task RequestRevisionTaskAsync(
        Guid taskId,
        Guid actorUserId,
        string comment,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct);

    Task<bool> RetryFinalizationAsync(Guid requestId, CancellationToken ct);
}
