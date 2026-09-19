namespace GubalLibrary;

internal static class PackCompatibility
{
    private static readonly HashSet<string> LifestreamSheets = new(StringComparer.OrdinalIgnoreCase)
    {
        "addon",
        "lobby",
        "eobjname",
        "warp",
        "transport/aetheryte",
        "transport/aetheryteishgard",
        "custom/001/cmndefhousingpersonalroomentrance_00178",
        "custom/003/houfixmansionentrance_00359",
        "custom/007/ctsmjientrance_00798",
    };

    internal static bool Excludes(string gamePath, bool lifestream)
    {
        if (!lifestream) return false;

        var relative = gamePath.StartsWith("exd/", StringComparison.OrdinalIgnoreCase) ? gamePath[4..] : gamePath;
        var slash = relative.LastIndexOf('/');
        var sheet = relative[..(slash + 1)] + PackParts.SheetOf(relative[(slash + 1)..]);
        return LifestreamSheets.Contains(sheet);
    }
}
