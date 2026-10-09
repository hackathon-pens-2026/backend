using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Routing.Services;

namespace SignIt.Modules.Letters.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/letters")]
public sealed class LettersController(LettersService letters, RoutingService routing, LetterSubmissionService submissions,
    LetterPreviewService previews) : ControllerBase
{
    [HttpPost("drafts")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult<DraftDto>> Create(SaveDraftRequest request, CancellationToken ct)
    {
        var draft = await letters.CreateAsync(User.GetUserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = draft.Id }, draft);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DraftDto>> Get(Guid id, CancellationToken ct)
        => Ok(await letters.GetAsync(User.GetUserId(), id, ct));

    [HttpPut("{id:guid}/draft")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult<DraftDto>> Edit(Guid id, EditDraftRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct)
        => Ok(await letters.EditAsync(User.GetUserId(), id, request, key, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<CancelLetterDto>> Cancel(Guid id, CancelLetterRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct)
        => Ok(await letters.CancelAsync(User.GetUserId(), id, request, key, ct));

    [HttpPost("routing-preview")]
    public async Task<ActionResult<IReadOnlyList<RoutingStage>>> Resolve(RoutingInput request, CancellationToken ct)
        => Ok(await routing.ResolveAsync(User.GetUserId(), request, ct));

    [HttpPost("{id:guid}/preview")]
    public async Task<ActionResult<LetterPreviewDto>> Preview(Guid id, PreviewLetterRequest request, CancellationToken ct)
    {
        var preview = await previews.QueueAsync(User.GetUserId(), id, request, ct);
        Response.Headers.CacheControl = "no-store";
        return preview.State == "Ready" ? Ok(preview) : AcceptedAtAction(nameof(GetPreview), new { id, jobId = preview.JobId }, preview);
    }

    [HttpGet("{id:guid}/previews/{jobId:guid}")]
    public async Task<ActionResult<LetterPreviewDto>> GetPreview(Guid id, Guid jobId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await previews.GetAsync(User.GetUserId(), id, jobId, ct));
    }

    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> DownloadPreview(Guid id, Guid documentId, CancellationToken ct)
    {
        var bytes = await previews.DownloadAsync(User.GetUserId(), id, documentId, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf");
    }

    [HttpPost("{id:guid}/submit")]
    [HttpPost("{id:guid}/resubmit")]
    public async Task<ActionResult<SubmissionDto>> Submit(Guid id, SubmitLetterRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct)
        => Ok(await submissions.SubmitAsync(User.GetUserId(), id, request, key, ct));
}
