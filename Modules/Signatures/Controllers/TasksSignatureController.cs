using System.Text.Json.Serialization;
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

    public TasksSignatureController(ISignatureWorkflowService workflow)
    {
        _workflow = workflow;
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reject(
        Guid id,
        [FromBody] TaskReasonRequest request,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        await _workflow.RejectTaskAsync(id, actorUserId, request.Reason, ip, userAgent, ct);
        return NoContent();
    }

    [HttpPost("request-revision")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RequestRevision(
        Guid id,
        [FromBody] TaskReasonRequest request,
        CancellationToken ct)
    {
        var actorUserId = User.GetUserId();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        await _workflow.RequestRevisionTaskAsync(id, actorUserId, request.Reason, ip, userAgent, ct);
        return NoContent();
    }
}

public sealed record TaskReasonRequest([property: JsonRequired] string Reason);
