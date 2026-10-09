using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Services;

namespace SignIt.Modules.Signatures.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/me/signature-qr")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Produces("application/json")]
public sealed class SignatureQrController : ControllerBase
{
    private readonly IUserSignatureQrService _qrService;

    public SignatureQrController(IUserSignatureQrService qrService)
    {
        _qrService = qrService;
    }

    [HttpGet]
    [ProducesResponseType<UserSignatureQrDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserSignatureQrDto>> GetMySignatureQr(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var qr = await _qrService.GetOrCreateForUserAsync(userId, ct);
        return Ok(qr);
    }

    [HttpGet("raw")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Produces("image/png")]
    public async Task<IActionResult> GetMySignatureQrRaw(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _qrService.GetRawImageAsync(userId, ct);
        if (result == null) return NotFound();

        Response.Headers.ETag = $"\"{result.Value.Sha256}\"";
        return File(result.Value.Bytes, result.Value.MimeType);
    }
}
