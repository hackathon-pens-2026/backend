using SignIt.Modules.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Signatures.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/tasks/{id:guid}")]
[Produces("application/json")]
public sealed class TasksSignatureController : ControllerBase
{
    private readonly ISignatureWorkflowService _workflow;
    private readonly WorkflowService _decisions;

    public TasksSignatureController(ISignatureWorkflowService workflow, WorkflowService decisions)
    {
        _workflow = workflow;
        _decisions = decisions;
    }

    [HttpPost("sign")]
    [ProducesResponseType<SignTaskResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SignTaskResultDto>> Sign(
        Guid id,
        [FromBody] SignTaskRequest request,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _workflow.ExecuteTaskActionAsync(
            id,
            actorUserId,
            WorkflowActionType.Sign,
            request,
            idempotencyKey,
            ip,
            userAgent,
            ct);

        return Ok(result);
    }

    [HttpPost("approve")]
    [ProducesResponseType<SignTaskResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SignTaskResultDto>> Approve(
        Guid id,
        [FromBody] SignTaskRequest request,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _workflow.ExecuteTaskActionAsync(
            id,
            actorUserId,
            WorkflowActionType.ApproveAndSign,
            request,
            idempotencyKey,
            ip,
            userAgent,
            ct);

        return Ok(result);
    }

    [HttpPost("acknowledge")]
    [ProducesResponseType<SignTaskResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SignTaskResultDto>> Acknowledge(
        Guid id,
        [FromBody] SignTaskRequest request,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _workflow.ExecuteTaskActionAsync(
            id,
            actorUserId,
            WorkflowActionType.Acknowledge,
            request,
            idempotencyKey,
            ip,
            userAgent,
            ct);

        return Ok(result);
    }

    [HttpPost("reject")]
    public Task<ActionResult<WorkflowMutationDto>> Reject(Guid id, WorkflowMutationRequest request, CancellationToken ct)
        => Decide(id, "reject", request, ct);

    [HttpPost("request-revision")]
    public Task<ActionResult<WorkflowMutationDto>> RequestRevision(Guid id, WorkflowMutationRequest request, CancellationToken ct)
        => Decide(id, "request-revision", request, ct);

    [HttpPost("defer")]
    public Task<ActionResult<WorkflowMutationDto>> Defer(Guid id, WorkflowMutationRequest request, CancellationToken ct)
        => Decide(id, "defer", request, ct);

    [HttpPost("resume")]
    public Task<ActionResult<WorkflowMutationDto>> Resume(Guid id, WorkflowMutationRequest request, CancellationToken ct)
        => Decide(id, "resume", request, ct);

    [HttpPost("delegate")]
    public Task<ActionResult<WorkflowMutationDto>> Delegate(Guid id, WorkflowMutationRequest request, CancellationToken ct)
        => Decide(id, "delegate", request, ct);

    [HttpPost("revoke-delegation")]
    public Task<ActionResult<WorkflowMutationDto>> RevokeDelegation(Guid id, WorkflowMutationRequest request, CancellationToken ct)
        => Decide(id, "revoke-delegation", request, ct);

    private async Task<ActionResult<WorkflowMutationDto>> Decide(Guid id, string action, WorkflowMutationRequest request, CancellationToken ct)
        => Ok(await _decisions.MutateAsync(User.GetUserId(), id, action, request,
            Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", ct));
}
