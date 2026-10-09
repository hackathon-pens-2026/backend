using PdfSharp.Fonts;

namespace SignIt.Modules.Signatures.Services;

public sealed class SignItFontResolver : IFontResolver
{
    private static string FontsFolder => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static readonly string[] SansSearchPaths =
    [
        Path.Combine(FontsFolder, "arial.ttf"),
        Path.Combine(FontsFolder, "Arial.ttf"),
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf"
    ];

    private static readonly string[] SerifSearchPaths =
    [
        Path.Combine(FontsFolder, "times.ttf"),
        Path.Combine(FontsFolder, "Times.ttf"),
        "/usr/share/fonts/truetype/liberation/LiberationSerif-Regular.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
        "/usr/share/fonts/TTF/DejaVuSerif.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSerif.ttf"
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
        var bold = faceName.Contains("Bold", StringComparison.Ordinal);
        var searchPaths = faceName.Contains("Serif", StringComparison.Ordinal) ? SerifSearchPaths : SansSearchPaths;

        foreach (var regularPath in searchPaths)
        {
            var path = bold ? BoldPath(regularPath) : regularPath;
            if (File.Exists(path))
            {
                return CachedFonts.GetOrAdd(faceName, _ => File.ReadAllBytes(path));
            }
        }

        return null;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var family = familyName.Contains("Times", StringComparison.OrdinalIgnoreCase) ? "SignItSerif" : "SignItSans";
        return new FontResolverInfo(isBold ? family + "Bold" : family, false, isItalic);
    }

    private static string BoldPath(string regularPath) => regularPath
        .Replace("arial.ttf", "arialbd.ttf", StringComparison.OrdinalIgnoreCase)
        .Replace("times.ttf", "timesbd.ttf", StringComparison.OrdinalIgnoreCase)
        .Replace("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", StringComparison.Ordinal)
        .Replace("DejaVuSerif.ttf", "DejaVuSerif-Bold.ttf", StringComparison.Ordinal)
        .Replace("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", StringComparison.Ordinal)
        .Replace("LiberationSerif-Regular.ttf", "LiberationSerif-Bold.ttf", StringComparison.Ordinal)
        .Replace("FreeSans.ttf", "FreeSansBold.ttf", StringComparison.Ordinal)
        .Replace("FreeSerif.ttf", "FreeSerifBold.ttf", StringComparison.Ordinal);
}
