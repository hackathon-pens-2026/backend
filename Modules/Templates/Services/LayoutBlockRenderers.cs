namespace SignIt.Modules.Templates.Services;

public sealed class PageBreakBlockRenderer : IRenderBlockRenderer
{
    public bool CanRender(string blockKind) => blockKind == "pageBreak";

    public void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander)
    {
        canvas.NewPage();
    }
}

public sealed class HeadingBlockRenderer : IRenderBlockRenderer
{
    public bool CanRender(string blockKind) => blockKind == "heading";

    public void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander)
    {
        canvas.Heading(expander(block.Text ?? ""));
    }
}

public sealed class ParagraphBlockRenderer : IRenderBlockRenderer
{
    public bool CanRender(string blockKind) => blockKind == "paragraph";

    public void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander)
    {
        canvas.Paragraph(expander(block.Text ?? ""));
    }
}

public sealed class RightAlignedBlockRenderer : IRenderBlockRenderer
{
    public bool CanRender(string blockKind) => blockKind == "right";

    public void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander)
    {
        canvas.Paragraph(expander(block.Text ?? ""), alignment: "right");
    }
}

public sealed class TableBlockRenderer : IRenderBlockRenderer
{
    public bool CanRender(string blockKind) => blockKind == "table";

    public void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander)
    {
        var expandedRows = (block.Rows ?? [])
            .Select(row => row.Select(expander).ToArray())
            .ToArray();
        canvas.Table(expandedRows);
    }
}

public sealed class RenderBlockRendererRegistry
{
    private readonly IReadOnlyList<IRenderBlockRenderer> _renderers;

    public RenderBlockRendererRegistry(IEnumerable<IRenderBlockRenderer>? renderers = null)
    {
        _renderers = renderers?.ToArray() ??
        [
            new PageBreakBlockRenderer(),
            new HeadingBlockRenderer(),
            new ParagraphBlockRenderer(),
            new RightAlignedBlockRenderer(),
            new TableBlockRenderer()
        ];
    }

    public void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander)
    {
        var renderer = _renderers.FirstOrDefault(r => r.CanRender(block.Kind))
            ?? throw new InvalidOperationException("Jenis blok template tidak didukung.");
        renderer.Render(block, canvas, expander);
    }
}
