namespace GubalLibrary;

/// <param name="Code">The pack's own language code, matched against a manifest's <c>language</c>.</param>
/// <param name="Name">Written in its own language, the way a language chooser is read.</param>
/// <param name="Source">What goes in the pack source box.</param>
/// <param name="Site">Where that pack is documented, or null.</param>
/// <param name="Issues">Where a wrong line in that pack is reported, or null.</param>
/// <param name="Published">False until a release exists at <paramref name="Source" />: the entry is listed, but nothing is offered for download.</param>
internal readonly record struct KnownPack(
    string Code,
    string Name,
    string? Source,
    string? Site,
    string? Issues,
    bool Published = true,
    string? SiteName = null)
{
    /// <summary>The manifest published beside the archive, which every release carries under its own name.</summary>
    public string? ManifestUrl =>
        this.Published && this.Source is { } s && s.LastIndexOf('/') is var slash && slash > 0
            ? s[..(slash + 1)] + PackManifest.FileName
            : null;
}

/// <summary>
///     The language packs the settings window offers, and where each one is reported to.
/// </summary>
/// <remarks>
///     A directory, not a distribution: an entry is an address the user still presses Install on.
///     A language with no <see cref="KnownPack.Source" /> is listed on purpose, so that it reads as
///     missing rather than unsupported. Nothing outside the settings window reads this.
/// </remarks>
internal static class KnownPacks
{
    /// <summary>Where the pack format is documented, for a language nobody has built yet.</summary>
    public const string Format = "https://eorzealocalized.com/localize/en.html";

    /// <summary>The plugin's own tracker. Not for a wrong line: see <see cref="KnownPack.Issues" />.</summary>
    public const string PluginIssues = "https://github.com/ashdam/gubal-library/issues";

    /// <summary>Where somebody who wants their language asks. Discussions must stay enabled on the
    /// repository or this is a 404.</summary>
    public const string Discussions = "https://github.com/ashdam/gubal-library/discussions";

    /// <summary>The author's Discord handle. Not a link: Discord has no address to open one at.</summary>
    public const string Discord = "miniashdam";

    /// <summary>Where a language's translators are pointed: one tutorial page per language.</summary>
    private const string Tutorial = "https://eorzealocalized.com/localize/";

    /// <summary>The page that walks a translator through building the pack for one language.</summary>
    public static string TutorialFor(string code) => Tutorial + code.ToLowerInvariant() + ".html";

    public static readonly KnownPack[] All =
    [
        new(
            "es-ES",
            "Español",
            "https://github.com/ashdam/ffxiv-language-pack-es-es/releases/latest/download/ffxiv-language-pack-es-es.zip",
            "https://eorzealocalized.com/es/",
            "https://github.com/ashdam/ffxiv-language-pack-es-es/issues",
            SiteName: "Eorzea en español"),
        new(
            "it",
            "Italiano",
            "https://github.com/ashdam/ffxiv-language-pack-it/releases/latest/download/ffxiv-language-pack-it.zip",
            "https://eorzealocalized.com/it/",
            "https://github.com/ashdam/ffxiv-language-pack-it/issues",
            SiteName: "Eorzea in italiano"),
        new(
            "pt-BR",
            "Português (Brasil)",
            "https://github.com/ashdam/ffxiv-language-pack-pt-br/releases/latest/download/ffxiv-language-pack-pt-br.zip",
            "https://eorzealocalized.com/pt-br/",
            "https://github.com/ashdam/ffxiv-language-pack-pt-br/issues",
            SiteName: "Eorzea em português"),
        new(
            "pl",
            "Polski",
            "https://github.com/ashdam/ffxiv-language-pack-pl/releases/latest/download/ffxiv-language-pack-pl.zip",
            "https://eorzealocalized.com/pl/",
            "https://github.com/ashdam/ffxiv-language-pack-pl/issues",
            SiteName: "Eorzea po polsku"),
    ];

    /// <summary>
    ///     The entry for a manifest's language code, or null when it names one not listed.
    /// </summary>
    /// <remarks>
    ///     The whole code first, then its primary subtag: a pack published as <c>es</c> before the
    ///     region was written into the code still belongs to the <c>es-ES</c> entry, and a manifest
    ///     saying <c>pt</c> is nearer to <c>pt-BR</c> than to nothing.
    /// </remarks>
    public static KnownPack? ForCode(string? code)
    {
        if (code is not { Length: > 0 })
        {
            return null;
        }

        foreach (var pack in All)
        {
            if (string.Equals(pack.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return pack;
            }
        }

        var primary = Primary(code);
        foreach (var pack in All)
        {
            if (string.Equals(Primary(pack.Code), primary, StringComparison.OrdinalIgnoreCase))
            {
                return pack;
            }
        }

        return null;

        static string Primary(string code)
        {
            var dash = code.IndexOfAny(['-', '_']);
            return dash > 0 ? code[..dash] : code;
        }
    }
}
