using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Routing.Services;

namespace SignIt.Modules.Letters.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/letters")]
public sealed class LettersController(LettersService letters, RoutingService routing, LetterSubmissionService submissions) : ControllerBase
{
    [HttpPost("drafts")]
    public async Task<ActionResult<DraftDto>> Create(SaveDraftRequest request, CancellationToken ct)
    {
        var draft = await letters.CreateAsync(User.GetUserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = draft.Id }, draft);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DraftDto>> Get(Guid id, CancellationToken ct)
        => Ok(await letters.GetAsync(User.GetUserId(), id, ct));

    [HttpPost("routing-preview")]
    public async Task<ActionResult<IReadOnlyList<RoutingStage>>> Resolve(RoutingInput request, CancellationToken ct)
        => Ok(await routing.ResolveAsync(User.GetUserId(), request, ct));

    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<SubmissionDto>> Submit(Guid id, SubmitLetterRequest request,
        [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct)
        => Ok(await submissions.SubmitAsync(User.GetUserId(), id, request, key, ct));
}
