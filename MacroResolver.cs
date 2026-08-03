using Dalamud.Plugin.Services;
using Lumina.Text.ReadOnly;

namespace GubalLibrary;

/// <summary>
///     Turns a corpus translation into the SeString the game will draw, or refuses it.
/// </summary>
/// <remarks>
///     <para>
///         Translations keep the game's own macro syntax — <c>&lt;if(gnum4,cansada,cansado)&gt;</c>,
///         <c>&lt;italic(1)&gt;</c> — rather than a bespoke token format, so one evaluator serves both
///         the lookup key and the injected value and there is no second syntax to keep in sync.
///     </para>
///     <para>
///         <b>The result is a <see cref="ReadOnlySeString" />, never a <see cref="string" />.</b> That
///         is the whole point of this type. FFXIV formatting is not characters: italics, colour and
///         icons are payload bytes, and <c>&lt;italic(1)&gt;</c> is only Lumina's readable spelling of
///         <c>02 1A 02 02 03</c>. Flattening the evaluated result with <c>ExtractText()</c> — which is
///         what this code used to do — deletes every one of them, because
///         <c>ReadOnlySeStringSpan.ExtractText</c> replaces non-text payloads with its
///         <c>macroPlaceholder</c> argument and that defaults to the empty string. The italicised ship
///         name in "captain of the Orion" reached the screen as plain text for exactly that reason.
///     </para>
///     <para>
///         <b>Resolved per display, not once at load.</b> The evaluator substitutes the player's name,
///         takes the branch that matches their gender and bakes in the Eorzean hour, so the answer is
///         only correct for the moment it is drawn.
///     </para>
///     <para>
///         <b>A line that will not evaluate is not injected.</b> Returning the raw macro text — the
///         previous behaviour — put <c>&lt;if(gnum4,…)&gt;</c> on screen, which is worse than the
///         English it replaced. This is the same rule <see cref="TranslationStore" /> already applies
///         to the key side, where a source that will not evaluate is dropped rather than indexed under
///         a string the game will never draw.
///     </para>
/// </remarks>
internal sealed class MacroResolver
{
    /// <summary>
    ///     How many distinct failures to name in the log before going quiet.
    /// </summary>
    /// <remarks>
    ///     Named rather than merely counted, and for the reason <see cref="TranslationStore" /> gives
    ///     for its own cap: a tally saying N lines are silently untranslated gives you no way to find
    ///     out which. A macro that will not evaluate is a defect in the corpus, so the text has to
    ///     reach the log or nobody can fix it.
    /// </remarks>
    private const int MaxReportedFailures = 20;

    private readonly ISeStringEvaluator evaluator;
    private readonly IPluginLog log;

    public MacroResolver(ISeStringEvaluator evaluator, IPluginLog log)
    {
        this.evaluator = evaluator;
        this.log = log;
    }

    /// <summary>How many translations have been refused since load. Reported by /gubal status.</summary>
    public int FailureCount { get; private set; }

    /// <summary>
    ///     Resolves a translation, or returns false and leaves the game's own text alone.
    /// </summary>
    /// <param name="target">The corpus translation, macro syntax intact.</param>
    /// <param name="resolved">The SeString to write, with formatting payloads preserved.</param>
    /// <param name="plain">
    ///     The same text with payloads removed. Callers need it for the font-size ladder and for logs:
    ///     the byte length is not the character count — every <c>&lt;italic&gt;</c> pair adds ten bytes
    ///     and no characters — and sizing text by its byte length shrinks the font for no reason.
    /// </param>
    public bool TryResolve(string target, out ReadOnlySeString resolved, out string plain)
    {
        // Most translations carry no macro; skip the evaluator rather than pay for it per line. The
        // test cannot be cheaper than this: searching for '<' also catches an escaped \<sigh>, which
        // is literal text, but that only costs a pointless evaluation — and the evaluator handles the
        // escape correctly, so the result is right either way. A target with no '<' at all cannot
        // contain an escape, which is what makes the fast path safe.
        if (!target.Contains('<', StringComparison.Ordinal))
        {
            resolved = target;
            plain = target;
            return true;
        }

        try
        {
            resolved = this.evaluator.EvaluateMacroString(target);
        }
        catch (Exception ex)
        {
            // Broad on purpose. Lumina throws MacroStringParseException for a malformed macro and,
            // less obviously, for a well-formed one whose name it does not support — so the set of
            // macros Lumina implements is in effect the corpus contract. Catching the base type keeps
            // that from turning into a crash the first time the corpus reaches for something new.
            this.Refuse(target, ex);
            resolved = default;
            plain = string.Empty;
            return false;
        }

        plain = resolved.ExtractText();

        // Whitespace, not merely empty: a translation that resolves to nothing visible would blank the
        // line rather than translate it, and a blank dialogue box reads as a broken game.
        if (string.IsNullOrWhiteSpace(plain))
        {
            this.Refuse(target, null);
            resolved = default;
            plain = string.Empty;
            return false;
        }

        return true;
    }

    private void Refuse(string target, Exception? ex)
    {
        this.FailureCount++;

        if (this.FailureCount > MaxReportedFailures)
        {
            return;
        }

        var shown = target.Length > 160 ? target[..160] + "…" : target;

        if (ex is null)
        {
            this.log.Warning("Translation resolved to nothing visible; not injected: {Target}", shown);
        }
        else
        {
            this.log.Warning(ex, "Translation will not evaluate; not injected: {Target}", shown);
        }
    }
}
