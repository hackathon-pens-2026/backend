using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Chat.DTOs;
using SignIt.Modules.Chat.Services;

namespace SignIt.Modules.Chat.Controllers;

[Authorize]
[EnableRateLimiting("chat")]
[ApiController]
[Route("api/v1/chat")]
[Produces("application/json")]
public sealed class ChatController(
    LetterChatOrchestrator orchestrator,
    AppDbContext db) : ControllerBase
{
    [HttpPost("sessions")]
    [ProducesResponseType<ChatSessionDetailDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatSessionDetailDto>> CreateOrResumeSession(
        [FromBody] CreateSessionRequest request,
        CancellationToken ct)
    {
        var result = await orchestrator.CreateOrResumeSessionAsync(User.GetUserId(), request, ct);
        return Ok(result);
    }

    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType<ChatSessionDetailDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatSessionDetailDto>> GetSession(
        Guid id,
        CancellationToken ct)
    {
        var result = await orchestrator.GetSessionDetailAsync(User.GetUserId(), id, ct);
        return Ok(result);
    }

    [HttpPost("sessions/{id:guid}/messages")]
    [ProducesResponseType<ChatTurnResponseDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatTurnResponseDto>> SendMessage(
        Guid id,
        [FromBody] SendChatMessageRequest request,
        CancellationToken ct)
    {
        var result = await orchestrator.ProcessTurnAsync(User.GetUserId(), id, request, ct);
        return Ok(result);
    }

    [HttpGet("sessions/by-draft/{draftId:guid}")]
    [ProducesResponseType<ChatSessionDetailDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatSessionDetailDto>> GetSessionByDraft(
        Guid draftId,
        CancellationToken ct)
    {
        var actor = User.GetUserId();
        var session = await db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LetterRequestId == draftId && x.UserId == actor, ct);

        if (session == null)
        {
            var detail = await orchestrator.CreateOrResumeSessionAsync(
                actor,
                new CreateSessionRequest(draftId, null),
                ct);
            return Ok(detail);
        }

        var result = await orchestrator.GetSessionDetailAsync(actor, session.Id, ct);
        return Ok(result);
    }
}
