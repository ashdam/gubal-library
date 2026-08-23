using Dalamud.Configuration;

namespace GubalLibrary;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Bumped to 2 when the page-flavoured names became language-pack ones.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Where the pack came from: a folder, a <c>.zip</c>, or a URL. Only a URL can be asked
    /// for a newer one, which is why this is kept beside <see cref="LanguagePackPath" />.</summary>
    public string PackSource { get; set; } = string.Empty;

    /// <summary>Absolute path to the folder actually served; empty serves nothing. Set by installing,
    /// not typed: an archive lands in the plugin directory, a folder source is served where it lies.</summary>
    public string LanguagePackPath { get; set; } = string.Empty;

    /// <summary>Serve the installed pack. Read once in the constructor, so it decides the next start
    /// rather than now — see <see cref="ExdRedirector" />.</summary>
    public bool ServeLanguagePack { get; set; }

    /// <summary>Fetch a newer pack during startup. Off by default: it spends bandwidth and holds the
    /// game's start unasked. Only honoured when Dalamud waits for plugins — see
    /// <see cref="DalamudBootWait" />.</summary>
    public bool AutoUpdatePack { get; set; }

    /// <summary>Log the Excel pages the client asks for, redirecting nothing. Diagnostic: the margin
    /// before the client's first read belongs to Dalamud's load order, and this is how to check a
    /// patch has not eaten it.</summary>
    public bool ProbeSqPack { get; set; }

    /// <summary>
    ///     The parts switched off. Empty serves everything.
    /// </summary>
    /// <remarks>
    ///     <b>What is stored is what is OFF</b>, so a sheet a later pack adds arrives translated
    ///     rather than silently withheld. <b>Keyed by sheet</b>, which is a fact about the game, and
    ///     not by the checkbox, which this plugin may re-cut. <b>Keys are lower case and nothing
    ///     relies on a comparer</b>: an <see cref="StringComparer.OrdinalIgnoreCase" /> set does not
    ///     survive the round trip through the config file, and case-insensitive until somebody
    ///     restarts is worse than never. Keys for sheets the pack lacks are kept, not pruned.
    /// </remarks>
    public HashSet<string> DisabledSheets { get; set; } = [];
}
