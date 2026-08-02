using System.Text.RegularExpressions;

namespace GubalLibrary;

/// <summary>
///     The lookup-key contract, shared by both sides of the join.
/// </summary>
/// <remarks>
///     <para>
///         The Talk addon does not expose the Excel row id of the line it is displaying — only the
///         finished string — so the runtime join is on text. The offline pipeline's internal game key
///         rides along in the corpus as metadata only.
///     </para>
///     <para>
///         <see cref="Normalize" /> is applied to <em>both</em> the string read out of the game and the
///         corpus side. It exists to absorb the ways two different libraries render the same SeString
///         to different characters — nothing more. Everything else, including player names and
///         conditional branches, is handled by evaluating the macro through the game's own evaluator,
///         so both sides arrive here already carrying identical literal text.
///     </para>
///     <para>
///         There is deliberately no player-name token. An earlier design substituted <c>{PLAYER}</c> on
///         both sides; once the corpus side resolves macros with the real character name, tokenizing
///         the runtime side would break the match rather than fix it.
///     </para>
/// </remarks>
internal static partial class TextKey
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Emphasis payloads come back from Dalamud's reader as literal asterisks — the italicised ship
        // name in "captain of the Orion" arrives as "captain of the *Orion*" — while Lumina's
        // ExtractText drops them. Strip so the two converge. Verified in game.
        // Inline speaker markup: "(-???-)Those men may be sworn Flames of Ul'dah..." — 6,830 lines
        // carry one. The extractor splits it off, and in a cutscene the game demonstrably does the
        // same before the string reaches the addon. Doing it here as well makes the match correct
        // WITHOUT having to know which of those two is true for any given sheet: whichever side still
        // carries the label loses it, and both arrive at the same key.
        text = SpeakerLabel().Replace(text, string.Empty);

        text = text.Replace("*", string.Empty, StringComparison.Ordinal);

        // Same class of problem, invisible on inspection. The game's "city<-->state" comes back from
        // Dalamud as an en dash (U+2013) but from Lumina as a plain hyphen (U+002D). One codepoint,
        // total miss.
        text = FoldDashes(text);

        // Line-break payloads arrive as \n, and stripping emphasis can leave doubled spaces behind.
        return WhitespaceRun().Replace(text, " ").Trim();
    }

    /// <summary>
    ///     Folds every Unicode dash variant to an ASCII hyphen.
    /// </summary>
    /// <remarks>
    ///     Covers hyphen (U+2010), non-breaking hyphen (U+2011), figure and en/em/horizontal dashes
    ///     (U+2012–U+2015) and the minus sign (U+2212).
    /// </remarks>
    private static string FoldDashes(string text)
    {
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        var changed = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var folded = c is '‐' or '‑' or '‒' or '–' or '—' or '―' or '−'
                ? '-'
                : c;

            changed |= folded != c;
            buffer[i] = folded;
        }

        return changed ? new string(buffer) : text;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    /// <summary>A leading <c>(-Name-)</c> speaker label.</summary>
    [GeneratedRegex(@"^\(-[^)]*?-\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerLabel();
}
