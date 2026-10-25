using CheapLoc;

namespace GubalLibrary;

internal static class CoreLanguage
{
    static CoreLanguage() => Loc.SetupWithFallbacks(typeof(CoreLanguage).Assembly);

    public static void Apply(string? translations)
    {
        if (translations is null)
            Loc.SetupWithFallbacks(typeof(CoreLanguage).Assembly);
        else
            Loc.Setup(translations, typeof(CoreLanguage).Assembly);
        PackParts.Invalidate();
    }

    public static string Localize(string key, string fallback) =>
        Loc.Localize(key, fallback, typeof(CoreLanguage).Assembly);
}
