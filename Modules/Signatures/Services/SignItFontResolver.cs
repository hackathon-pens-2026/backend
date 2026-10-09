using PdfSharp.Fonts;

namespace SignIt.Modules.Signatures.Services;

public sealed class SignItFontResolver : IFontResolver
{
    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "Arial.ttf"),
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf"
    ];

    private static byte[]? _cachedFontBytes;
    private static bool _initialized;
    private static readonly object Lock = new();

    public static void EnsureRegistered()
    {
        if (_initialized) return;
        lock (Lock)
        {
            if (_initialized) return;
            try
            {
                if (GlobalFontSettings.FontResolver == null)
                {
                    GlobalFontSettings.FontResolver = new SignItFontResolver();
                }
            }
            catch
            {
                // Already assigned or custom resolver present
            }
            _initialized = true;
        }
    }

    public byte[]? GetFont(string faceName)
    {
        if (_cachedFontBytes != null) return _cachedFontBytes;

        foreach (var path in SearchPaths)
        {
            if (File.Exists(path))
            {
                _cachedFontBytes = File.ReadAllBytes(path);
                return _cachedFontBytes;
            }
        }

        return null;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        return new FontResolverInfo("SignItDefaultFont");
    }
}
