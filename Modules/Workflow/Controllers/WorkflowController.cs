using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Workflow.Services;
using SignIt.Modules.Signatures.Services;

namespace SignIt.Modules.Workflow.Controllers;

[Authorize]
[ApiController]
[Route("api/v1")]
public sealed class WorkflowController(WorkflowQueryService queries) : ControllerBase
{
    [HttpGet("letters/{id:guid}/signed-document")]
    public async Task<IActionResult> SignedDocument(Guid id, [FromServices] SignatureDocumentService documents, CancellationToken ct)
    {
        var bytes = await queries.SignedDocumentAsync(User.GetUserId(), id, documents, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf");
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<WorkflowQueueDto>> Queue(CancellationToken ct, int page = 1, int pageSize = 20)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await queries.QueueAsync(User.GetUserId(), page, pageSize, ct));
    }
    [HttpGet("tasks/{id:guid}")]
    public async Task<ActionResult<WorkflowTaskDto>> TaskDetail(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await queries.GetAsync(User.GetUserId(), id, ct));
    }
    [HttpGet("letters/{id:guid}/workflow")]
    public async Task<ActionResult<LetterWorkflowDto>> Letter(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await queries.LetterAsync(User.GetUserId(), id, ct));
    }
    [HttpGet("tasks/{id:guid}/document")]
    public async Task<IActionResult> Document(Guid id, CancellationToken ct)
    {
        var bytes = await queries.DownloadAsync(User.GetUserId(), id, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf");
    }

    [HttpGet("tasks/{id:guid}/delegate-candidates")]
    public async Task<ActionResult<DelegateCandidateDto[]>> Candidates(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await queries.CandidatesAsync(User.GetUserId(), id, ct));
    }
}
