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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> CachedFonts = new();
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
        if (CachedFonts.TryGetValue(faceName, out var cached)) return cached;

        foreach (var regularPath in SearchPaths)
        {
            var path = faceName == "SignItBoldFont" ? BoldPath(regularPath) : regularPath;
            if (File.Exists(path))
            {
                return CachedFonts.GetOrAdd(faceName, _ => File.ReadAllBytes(path));
            }
        }

        return null;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        return new FontResolverInfo(isBold ? "SignItBoldFont" : "SignItDefaultFont", false, isItalic);
    }

    private static string BoldPath(string regularPath) => regularPath
        .Replace("arial.ttf", "arialbd.ttf", StringComparison.OrdinalIgnoreCase)
        .Replace("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", StringComparison.Ordinal)
        .Replace("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", StringComparison.Ordinal)
        .Replace("FreeSans.ttf", "FreeSansBold.ttf", StringComparison.Ordinal);
}
