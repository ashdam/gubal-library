using Dalamud.Configuration;

namespace GubalLibrary;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Bumped to 2 when the page-flavoured names became language-pack ones.</summary>
    public int Version { get; set; } = 2;

    /// <summary>
    ///     Where the user got the pack from: a folder, a <c>.zip</c>, or a URL to one.
    /// </summary>
    /// <remarks>
    ///     Kept alongside <see cref="LanguagePackPath" /> rather than replacing it, because they
    ///     answer different questions. This one is where to look for a newer generation, and only a
    ///     URL can answer that; the other is what to serve at the next start, and after an archive is
    ///     unpacked that is somewhere else entirely.
    /// </remarks>
    public string PackSource { get; set; } = string.Empty;

    /// <summary>
    ///     Absolute path to the folder actually served. Empty means nothing is served.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set by installing, not typed. For an archive it is the plugin's own directory, where
    ///         <see cref="PackInstaller" /> unpacked it; for a folder source it is that folder,
    ///         because copying it would make a second copy that no build writes to.
    ///     </para>
    ///     <para>
    ///         Nothing is bundled with the plugin: a pack is tens of megabytes across thousands of
    ///         files, it is derived from the game's own data, and it is regenerated whenever the game
    ///         is patched.
    ///     </para>
    /// </remarks>
    public string LanguagePackPath { get; set; } = string.Empty;

    /// <summary>
    ///     Serve the installed pack in place of the game's own text. Off until one is chosen.
    /// </summary>
    /// <remarks>
    ///     Read once, in the constructor, and never again. The client reads its Excel sheets about two
    ///     seconds after plugins load and keeps them for the session, so this decides what happens at
    ///     the next start rather than now — see <see cref="ExdRedirector" />.
    /// </remarks>
    public bool ServeLanguagePack { get; set; }

    /// <summary>
    ///     Hook the client's archive reads and log the Excel pages it asks for, redirecting nothing.
    /// </summary>
    /// <remarks>
    ///     Diagnostic, off by default. It answered whether this plugin could redirect files itself
    ///     rather than going through Penumbra — it attaches 2.1 seconds before the client's first
    ///     Excel read, so it can, and <see cref="ExdRedirector" /> now does. It is kept because that
    ///     margin is a property of Dalamud's load order rather than of this code, and the cheapest way
    ///     to find out that a patch or a settings change has eaten it is to look again.
    /// </remarks>
    public bool ProbeSqPack { get; set; }

}
