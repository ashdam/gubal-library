namespace GubalLibrary;

/// <param name="Code">The pack's own language code, matched against a manifest's <c>language</c>.</param>
/// <param name="Name">Written in its own language, the way a language chooser is read.</param>
/// <param name="Source">What goes in the pack source box. Null when nobody publishes one yet.</param>
/// <param name="Site">Where that pack is documented, or null.</param>
/// <param name="Issues">Where a wrong line in that pack is reported, or null.</param>
internal readonly record struct KnownPack(
    string Code,
    string Name,
    string? Source,
    string? Site,
    string? Issues);

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
    public const string Format = "https://github.com/ashdam/gubal-library/blob/main/LANGUAGE-PACK.md";

    /// <summary>The plugin's own tracker. Not for a wrong line: see <see cref="KnownPack.Issues" />.</summary>
    public const string PluginIssues = "https://github.com/ashdam/gubal-library/issues";

    /// <summary>Where somebody who wants their language asks. Discussions must stay enabled on the
    /// repository or this is a 404.</summary>
    public const string Discussions = "https://github.com/ashdam/gubal-library/discussions";

    /// <summary>The author's Discord handle. Not a link: Discord has no address to open one at.</summary>
    public const string Discord = "miniashdam";

    public static readonly KnownPack[] All =
    [
        new(
            "es",
            "Español",
            "https://github.com/ashdam/ffxiv-language-pack-es/releases/latest/download/ffxiv-language-pack-es.zip",
            "https://eorzea-in-spanish.ashdam.workers.dev/",
            "https://github.com/ashdam/ffxiv-language-pack-es/issues"),
        new("it", "Italiano", null, null, null),
        new("pt", "Português", null, null, null),
    ];

    /// <summary>The entry for a manifest's language code, or null when it names one not listed.</summary>
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

        return null;
    }
}
