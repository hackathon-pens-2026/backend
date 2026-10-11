using SignIt.Modules.Letters.Services;

namespace SignIt.Modules.Templates.Services;

public interface ILayoutCanvas : IDisposable
{
    void NewPage();
    void Space(double points);
    void Heading(string text);
    void Paragraph(string text, double size = 11, bool bold = false, string alignment = "left");
    void Table(string[][] rows);
    SignatureSlot[] SignaturePages();
    byte[] Save();
}

public interface IRenderBlockRenderer
{
    bool CanRender(string blockKind);
    void Render(RenderBlock block, ILayoutCanvas canvas, Func<string, string> expander);
}
