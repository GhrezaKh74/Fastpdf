using FastPdfWriterStream.Fonts;

namespace FastPdfWriterStream.Tests;

internal static class TestFonts
{
    public static readonly string Root = FindRoot();
    public static readonly string FontsDir = Path.Combine(Root, "fonts");
    public static readonly string TemplatesDir = Path.Combine(Root, "templates");
    public static readonly string Regular = Path.Combine(FontsDir, "Vazirmatn-Regular.ttf");
    public static readonly string Bold = Path.Combine(FontsDir, "Vazirmatn-Bold.ttf");

    private static TrueTypeFont? _regular;
    public static TrueTypeFont RegularFont => _regular ??= TrueTypeFont.FromFile(Regular);

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "fonts"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("Repository root (with fonts/) not found.");
    }
}
