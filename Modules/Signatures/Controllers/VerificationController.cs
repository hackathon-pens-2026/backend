using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Services;

namespace SignIt.Modules.Signatures.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/v1/verify")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Produces("application/json")]
public sealed class VerificationController : ControllerBase
{
    private readonly IPublicVerificationService _verification;

    public VerificationController(IPublicVerificationService verification)
    {
        _verification = verification;
    }

    [HttpGet("{code}")]
    [ProducesResponseType<PublicVerificationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PublicVerificationDto>> VerifyByCode(string code, CancellationToken ct)
    {
        var result = await _verification.VerifyByCodeAsync(code, ct);
        return Ok(result);
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<VerifyUploadResultDto>(StatusCodes.Status200OK)]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<VerifyUploadResultDto>> VerifyByUpload(
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { code = "file_required", message = "File PDF wajib disertakan untuk verifikasi." });
        }

        await using var stream = file.OpenReadStream();
        var result = await _verification.VerifyByUploadAsync(stream, file.Length, ct);
        return Ok(result);
    }
}
