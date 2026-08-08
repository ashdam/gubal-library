using Dalamud.Configuration;

namespace GubalLibrary;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Bumped to 2 when the page-flavoured names became language-pack ones.</summary>
    public int Version { get; set; } = 2;

    /// <summary>
    ///     Absolute path to the installed language pack. Empty means nothing is served.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A folder today, holding the rebuilt <c>.exd</c> pages and the <c>gubal-manifest.json</c>
    ///         that describes them — which is also exactly what a distributable zip would unpack to,
    ///         so a future installer changes how the folder gets there and not what is in it.
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

    /// <summary>Previous name of <see cref="LanguagePackPath" />. Read once, then never written.</summary>
    /// <remarks>
    ///     Kept solely so an existing install does not silently forget where its pack is. Dropping the
    ///     property instead would deserialize to empty and present as "no language pack loaded", which
    ///     is indistinguishable from a broken plugin and would send people looking in the wrong place.
    /// </remarks>
    [Obsolete("Migrated into LanguagePackPath by Migrate().")]
    public string? PagesPath { get; set; }

    /// <inheritdoc cref="PagesPath" />
    [Obsolete("Migrated into ServeLanguagePack by Migrate().")]
    public bool? ServePages { get; set; }

    /// <summary>Folds a version 1 configuration into the current names.</summary>
    /// <returns>Whether anything moved, so the caller can save only when it did.</returns>
    public bool Migrate()
    {
#pragma warning disable CS0618
        var moved = false;

        if (this.LanguagePackPath.Length == 0 && this.PagesPath is { Length: > 0 } path)
        {
            this.LanguagePackPath = path;
            moved = true;
        }

        if (this.ServePages is { } serve)
        {
            this.ServeLanguagePack = serve;
            moved = true;
        }

        this.PagesPath = null;
        this.ServePages = null;
        this.Version = 2;
        return moved;
#pragma warning restore CS0618
    }
}
