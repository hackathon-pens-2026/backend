namespace SignIt.Modules.Templates.Services;

public interface ILetterTemplateRenderer
{
    RenderedPreview Render(PreviewRenderInput input, CancellationToken ct);
}
