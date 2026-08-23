using System.Text.Json;
using System.Text.Json.Serialization;

namespace GubalLibrary;

/// <summary>
///     What a folder of translated pages says about itself: <c>gubal-manifest.json</c>, written by
///     <c>Tools/PackBuilder</c> beside the pages.
/// </summary>
/// <remarks>
///     Half of it is authored in the corpus — name, language, author — and half is stamped by the
///     build, which is the half that must never be typed: a version a person maintains is wrong the
///     first time somebody forgets. Every field belongs to somebody else's work, so the window shows
///     them rather than assuming them.
/// </remarks>
internal sealed class PackManifest
{
    public const string FileName = "gubal-manifest.json";

    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>Language of the translation, not of the slot the pages overwrite.</summary>
    /// <remarks>
    ///     The game has no Spanish slot, so a Spanish pack ships <c>_en.exd</c> files and anyone
    ///     reading the folder without this concludes it is English.
    /// </remarks>
    [JsonPropertyName("language")] public string? Language { get; init; }

    [JsonPropertyName("languageName")] public string? LanguageName { get; init; }

    [JsonPropertyName("author")] public string? Author { get; init; }

    /// <summary>
    ///     A manifest to fetch to find out whether a newer generation exists. Optional.
    /// </summary>
    /// <remarks>
    ///     <b>The pack says where to look; the plugin imposes nothing.</b> Deriving it from the
    ///     download address by convention would require every publisher in every language to lay
    ///     their hosting out the same way. Travelling inside the pack also means it updates itself,
    ///     so a publisher can move hosts between releases. Leaving it out is fine and never nagged;
    ///     filling it in is a promise to keep answering, so a failure to is worth reporting.
    /// </remarks>
    [JsonPropertyName("updateUrl")] public string? UpdateUrl { get; init; }

    /// <summary>Which generation this is, stamped to the minute at build time.</summary>
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

    /// <summary>Share of the OPENED sheets that carries a translation, 0 before the count existed.</summary>
    /// <remarks>
    ///     Not "share of the game": a sheet nobody has started on is absent from both sides, which
    ///     flatters the number, so the window says which denominator this is.
    /// </remarks>
    public double TranslatedFraction => this.Rows > 0 ? (double)this.Lines / this.Rows : 0d;

    /// <summary>Reads the manifest out of a page directory, or says why it could not.</summary>
    /// <remarks>
    ///     A missing manifest is a refusal, not a default. The likeliest cause is a folder chosen one
    ///     level from the one the build wrote, and naming the file is what leads people there.
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
