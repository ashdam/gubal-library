using Dalamud.Configuration;

namespace GubalLibrary;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch. When false, no injection and no lookups happen.</summary>
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
