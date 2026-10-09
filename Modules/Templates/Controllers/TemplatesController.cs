using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Templates.Services;

namespace SignIt.Modules.Templates.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/templates")]
public sealed class TemplatesController(TemplateCatalog catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LetterTemplateDto>>> GetAll(CancellationToken ct)
        => Ok(await catalog.GetAllAsync(ct));

    [HttpGet("{typeId}")]
    public async Task<ActionResult<LetterTemplateDto>> Get(string typeId, CancellationToken ct)
    {
        var template = (await catalog.GetAllAsync(ct)).SingleOrDefault(x => x.TypeId == typeId);
        return template == null ? NotFound() : Ok(template);
    }
}
