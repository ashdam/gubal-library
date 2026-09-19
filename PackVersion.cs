using CheapLoc;

namespace GubalLibrary;

internal static class PackVersion
{
    public static string? Error(string? builtFor, string? running)
    {
        if (string.IsNullOrWhiteSpace(builtFor) || string.IsNullOrWhiteSpace(running))
        {
            return Loc.Localize("Pack.GameVersionUnknown",
                "The language pack's game version could not be checked. Translation is disabled. "
                + "Install a language pack built for the current game version.");
        }

        return string.Equals(builtFor, running, StringComparison.Ordinal)
            ? null
            : string.Format(Loc.Localize("Pack.GameVersionMismatch",
                "This language pack was built for game {0}; you are running {1}. Translation is disabled. "
                + "Update the language pack to a version built for this game."),
                builtFor, running);
    }
}
