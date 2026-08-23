using System.Reflection;
using CheapLoc;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Puts this window in the language Dalamud is set to, or leaves it in English.
/// </summary>
/// <remarks>
///     <para>
///         <b>No language setting of our own.</b> The code comes from
///         <see cref="IDalamudPluginInterface.UiLanguage" /> and changes with it, because a plugin
///         that asks again what the user has already told Dalamud is one more place for the two
///         answers to disagree.
///     </para>
///     <para>
///         <b>English is not a file.</b> Every string is written as
///         <c>Loc.Localize(key, "the English")</c>, so the source language travels in the code and a
///         missing file, a missing key or an empty translation all fall back to it. What ships as
///         JSON is only what has been translated.
///     </para>
///     <para>
///         <b>Only what Dalamud can be set to is worth shipping.</b> Its language list —
///         <c>Localization.ApplicableLangCodes</c>, plus English — holds de, ja, fr, it, es, ko, no,
///         ru, zh and tw. Portuguese and Polish are not in it and cannot be chosen, so a pack of
///         either would never be selected by anybody; adding one of the ten that are is a new file
///         in <c>loc/</c> and no code at all.
///     </para>
///     <para>
///         <b>Set up before anything reads a string.</b> CheapLoc answers <c>#Key</c>, not the
///         fallback, for an assembly it has never been set up for — so the first thing the plugin
///         does is call this, and the failure mode if it ever stops doing so is a window full of
///         hash signs rather than English.
///     </para>
/// </remarks>
internal static class Language
{
    /// <summary>The translations that ship inside the DLL, by Dalamud language code.</summary>
    private static readonly string[] Shipped = ["es", "it"];

    /// <summary>What the window is currently drawn in. English until told otherwise.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>
    ///     Loads the translation for a Dalamud language code, or the English fallbacks.
    /// </summary>
    /// <remarks>
    ///     Anything that goes wrong ends in English rather than in an exception: this runs before the
    ///     redirection is installed, and a plugin that fails to construct takes the whole translation
    ///     of the game down with it over a settings window's wording.
    /// </remarks>
    public static void Apply(string? code, IPluginLog log)
    {
        Current = "en";

        try
        {
            if (code is { Length: > 0 } wanted && Shipped.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            {
                var name = $"GubalLibrary.loc.{wanted.ToLowerInvariant()}.json";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    Loc.Setup(reader.ReadToEnd());
                    Current = wanted.ToLowerInvariant();
                    log.Information("Settings window language: {Code}.", Current);
                    return;
                }

                log.Warning("No {Name} is embedded in this build; falling back to English.", name);
            }

            Loc.SetupWithFallbacks();
        }
        catch (Exception e)
        {
            log.Error(e, "Could not load the settings window's translation; using English.");
            Loc.SetupWithFallbacks();
        }
    }
}
