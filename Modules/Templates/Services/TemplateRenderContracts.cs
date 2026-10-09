using SignIt.Modules.Letters.Services;
using SignIt.Modules.Routing.Services;

namespace SignIt.Modules.Templates.Services;

public sealed record RenderBlock(string Kind, string? Text = null, string[][]? Rows = null);
public sealed record TemplateRenderLayout(string RendererVersion, string? CoverTitle, RenderBlock[] Blocks);
public sealed record RenderParticipant(RoutingStage Stage, string? NimNip);
public sealed record PreviewRenderInput(string TypeId, string TemplateVersionId, string TemplateAssetHash,
    string Title, Guid OrganizationId, Guid? ResourceId, Dictionary<string, string> Fields,
    RenderParticipant[] Participants, TemplateRenderLayout Layout);
public sealed record RenderedPreview(byte[] Bytes, SignatureSlot[] Slots);

public interface ILetterTemplateRenderer
{
    RenderedPreview Render(PreviewRenderInput input, CancellationToken ct);
}
