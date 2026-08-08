using System.Text.Json;
using System.Text.Json.Serialization;

namespace GubalLibrary;

/// <summary>
///     What a folder of translated pages says about itself.
/// </summary>
/// <remarks>
///     <para>
///         Written by <c>Tools/ExdRedirect</c> as <c>gubal-manifest.json</c> beside the pages. Half
///         of it is authored in <c>corpus-es/pack.json</c> — name, language, author — and half is
///         stamped by the build, which is the half that must never be typed by hand: a version a
///         person maintains is a version that is wrong the first time somebody forgets.
///     </para>
///     <para>
///         The plugin ships no translations, so every field here belongs to somebody else's work.
///         That is why it is displayed rather than assumed: a user who cannot see which pack is
///         loaded and which generation of it cannot tell a rebuild that took from one that did not,
///         and that question has come up more than any other.
///     </para>
/// </remarks>
internal sealed class PackManifest
{
    public const string FileName = "gubal-manifest.json";

    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>Language code of the translation, not of the slot the pages overwrite.</summary>
    /// <remarks>
    ///     The game has no Spanish slot, so a Spanish pack ships <c>_en.exd</c> files. Anyone reading
    ///     the folder without this field concludes the pack is English, which is exactly backwards.
    /// </remarks>
    [JsonPropertyName("language")] public string? Language { get; init; }

    [JsonPropertyName("languageName")] public string? LanguageName { get; init; }

    [JsonPropertyName("author")] public string? Author { get; init; }

    /// <summary>
    ///     A manifest to fetch to find out whether a newer generation of this pack exists.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The pack says where to look; the plugin imposes nothing.</b> An earlier design
    ///         derived this from the download address by convention — <c>…/x.zip</c> implies
    ///         <c>…/x.json</c> — which quietly required every publisher in every language to lay their
    ///         hosting out the way this project happened to. A Spanish pack and an Italian one have no
    ///         reason to share a repository, let alone a filename scheme.
    ///     </para>
    ///     <para>
    ///         Because it travels inside the pack, it also updates itself: whatever is installed next
    ///         brings its own address, so a publisher can move hosts between releases and the people
    ///         who already installed one follow along. Nothing about it is stored in this plugin's
    ///         configuration, which is what makes that true rather than merely intended.
    ///     </para>
    ///     <para>
    ///         <b>Optional, and unreachable is not the same as absent.</b> A test build, or an author
    ///         who has nowhere to host a manifest, simply leaves it out and is never nagged. Filling
    ///         it in is a promise to keep answering at that address, so failing to is worth telling
    ///         the user about — from their side the two look identical, and one of them means their
    ///         translation has quietly stopped receiving corrections.
    ///     </para>
    ///     <para>
    ///         There is deliberately no companion field saying where the archive lives. The user
    ///         typed that in to install the pack, so the manifest repeating it would be a second copy
    ///         of a fact that can disagree with the first. The contract it implies is the right one
    ///         anyway: publish successive versions at a stable address.
    ///     </para>
    /// </remarks>
    [JsonPropertyName("updateUrl")] public string? UpdateUrl { get; init; }

    /// <summary>Which generation of the translation this is, stamped to the minute at build time.</summary>
    [JsonPropertyName("translationVersion")] public string? TranslationVersion { get; init; }

    /// <summary>The patch the pages were rebuilt from. The one field that gates serving them.</summary>
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; init; }


    [JsonPropertyName("pages")] public int Pages { get; init; }

    /// <summary>Rows carrying a translation.</summary>
    [JsonPropertyName("lines")] public int Lines { get; init; }

    /// <summary>Rows in the rebuilt pages, translated or not — the denominator for <see cref="Lines" />.</summary>
    [JsonPropertyName("rows")] public int Rows { get; init; }

    /// <summary>Display name, falling back through what the pack actually filled in.</summary>
    public string DisplayName => this.Name ?? this.LanguageName ?? this.Language ?? "Unnamed language pack";

    /// <summary>Share of the opened sheets that carries a translation, 0 when the build predates the count.</summary>
    /// <remarks>
    ///     Deliberately not "share of the game". The denominator counts only sheets the corpus has
    ///     touched, so a sheet nobody has started on is absent from both sides — which flatters the
    ///     number. The window says so rather than showing a percentage that reads as coverage.
    /// </remarks>
    public double TranslatedFraction => this.Rows > 0 ? (double)this.Lines / this.Rows : 0d;

    /// <summary>
    ///     Reads the manifest out of a page directory, or says why it could not.
    /// </summary>
    /// <remarks>
    ///     A missing manifest is a refusal rather than a default, and the message names the tool that
    ///     writes it. The likeliest cause by far is a folder chosen one level up or down from the one
    ///     the build wrote, and "no gubal-manifest.json here" is the only wording that has ever led
    ///     anyone straight to that.
    /// </remarks>
    public static (PackManifest? Manifest, string? Error) Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return (null, $"No {FileName} here, so this is not a language pack. Point at the folder that holds it.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<PackManifest>(stream);
            return manifest is null
                ? (null, $"{FileName} is empty.")
                : (manifest, null);
        }
        catch (Exception e)
        {
            return (null, $"{FileName} could not be read: {e.Message}");
        }
    }
}


