using Dalamud.Configuration;

namespace GubalLibrary;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    ///     Text injection. When false, no lookups and nothing is swapped into a UI node.
    /// </summary>
    /// <remarks>
    ///     Was documented as the plugin's master switch, and was one while injection was the only way
    ///     Spanish reached the screen. It is not one any more: pages registered with Penumbra keep
    ///     being served whatever this says, because a redirection lives in Penumbra rather than here.
    ///     See <see cref="ServePages" />.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Absolute path to the translation file. Empty means <c>corpus.json</c> in the plugin config
    ///     directory.
    /// </summary>
    /// <remarks>
    ///     The corpus is deliberately not bundled with the plugin — it is far too large and changes
    ///     independently of the code. This lets it live wherever it is actually maintained, e.g.
    ///     alongside the extractor output, without copying it on every regeneration.
    /// </remarks>
    public string CorpusPath { get; set; } = string.Empty;

    /// <summary>
    ///     Absolute path to the folder of rebuilt <c>.exd</c> pages. Empty means the file route is off.
    /// </summary>
    /// <remarks>
    ///     The same reasoning as <see cref="CorpusPath" />, for the same reason: the pages are tens of
    ///     megabytes across thousands of files, they are derived from the game's own data, and they
    ///     are regenerated on a different cadence from the code. The folder is what
    ///     <c>Tools\ExdRedirect</c> writes, manifest included.
    /// </remarks>
    public string PagesPath { get; set; } = string.Empty;

    /// <summary>Register the pages with Penumbra on load. Off until someone points at a folder.</summary>
    public bool ServePages { get; set; }

    /// <summary>Replace the speaker name too. Off by default — most NPC names are proper nouns.</summary>
    public bool TranslateNpcNames { get; set; }

    /// <summary>Append unmatched keys to misses.jsonl. On by default during development.</summary>
    public bool LogMisses { get; set; } = true;

    /// <summary>
    ///     Log the live event-handler state for each new Talk line.
    /// </summary>
    /// <remarks>
    ///     Off by default. Answers whether the game's own line identifier is reachable while the Talk
    ///     window is open — see <see cref="EventProbe" /> for why that is still an open question and
    ///     what a positive result would buy.
    /// </remarks>
    public bool ProbeEvents { get; set; }

    /// <summary>
    ///     Text the addon finder is hunting for. Empty means it is off.
    /// </summary>
    /// <remarks>
    ///     Persisted rather than held in memory because the hunt and the rebuild compete. Dev plugins
    ///     auto-reload the moment the DLL changes, which clears anything transient — so a needle set
    ///     before going into a duty was silently lost by whatever unrelated build happened meanwhile,
    ///     and the run was wasted. Saving it means the search survives, and a search that survives can
    ///     be left armed for days until the line happens to appear.
    /// </remarks>
    public string FindText { get; set; } = string.Empty;
}
