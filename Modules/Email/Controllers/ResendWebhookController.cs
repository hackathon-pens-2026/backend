using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Infrastructure.Errors;
using SignIt.Modules.Email.Services;

namespace SignIt.Modules.Email.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/resend")]
[Produces("application/json")]
public sealed class ResendWebhookController(IEmailWebhookVerifier verifier, EmailWebhookProcessor processor) : ControllerBase
{
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string payload;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            payload = await reader.ReadToEndAsync(ct);

        var verification = verifier.Verify(Request.Headers["svix-id"].ToString(),
            Request.Headers["svix-timestamp"].ToString(), Request.Headers["svix-signature"].ToString(), payload);
        if (verification == WebhookVerificationResult.NotConfigured)
            return ProblemResult(StatusCodes.Status503ServiceUnavailable, "webhook_not_configured",
                "Webhook Resend belum dikonfigurasi.");
        if (verification == WebhookVerificationResult.Invalid)
            return ProblemResult(StatusCodes.Status401Unauthorized, "webhook_invalid_signature",
                "Signature webhook tidak valid.");

        var eventId = Request.Headers["svix-id"].ToString();
        // The event id itself is untrusted input; the processor stores it only after signature verification.
        return (await processor.ProcessAsync(eventId, payload, ct)) switch
        {
            WebhookProcessingResult.InvalidPayload
                => ProblemResult(StatusCodes.Status400BadRequest, "webhook_invalid_payload",
                    "Payload webhook tidak valid."),
            WebhookProcessingResult.Duplicate => Ok(new { status = "duplicate" }),
            _ => Ok(new { status = "processed" })
        };
    }

    private IActionResult ProblemResult(int status, string code, string message)
        => new ObjectResult(ApiProblems.Create(HttpContext, status, code, message))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };
}
