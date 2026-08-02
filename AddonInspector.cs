using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Logs an addon's value array and text nodes, for bringing a new addon under translation.
/// </summary>
/// <remarks>
///     <para>
///         This existed once as a <c>Talk</c>-specific dump, was used to establish that the addon
///         carries no text id, and was then deleted as answered scaffolding. That was a mistake in
///         kind, not degree: the question it answered was specific, but the tool is general. Within
///         the hour the next addon needed exactly it. Rebuilt so it applies to any addon by name.
///     </para>
///     <para>
///         The two things needed to translate an addon are which <c>AtkValue</c> holds the string and
///         which node draws it, and neither can be read out of FFXIVClientStructs —
///         <c>AddonTalkSubtitle</c> declares an <c>AtkValues</c> array and no named text nodes at all.
///         Both are observable in one pass, so observe them rather than guessing and then debugging
///         the guess.
///     </para>
/// </remarks>
internal static unsafe class AddonInspector
{
    /// <summary>
    ///     Dumps every value and every text node of an addon once.
    /// </summary>
    /// <param name="log">Dalamud's logger.</param>
    /// <param name="addonName">Name of the addon, for the log line.</param>
    /// <param name="addon">The addon, for walking its nodes. May be null.</param>
    /// <param name="values">The refresh value array.</param>
    /// <param name="count">Number of values.</param>
    public static void Dump(
        IPluginLog log,
        string addonName,
        AtkUnitBase* addon,
        AtkValue* values,
        int count)
    {
        log.Information("=== {Addon}: {Count} value(s) ===", addonName, count);

        for (var i = 0; i < count; i++)
        {
            var value = values[i];
            var asString = AtkText.HoldsString(value) ? AtkText.ReadString(value) : "-";

            log.Information("  [{Index}] {Type} int={Int} str={String}", i, value.Type, value.Int, asString);
        }

        if (addon is null)
        {
            return;
        }

        // Node ids are the other half. Walking them beats guessing: the id that draws the text is
        // whatever id currently holds it, and printing all of them shows the layout in one go.
        log.Information("  --- text nodes ---");
        for (var id = 1u; id <= 24u; id++)
        {
            var node = addon->GetTextNodeById(id);
            if (node is null || node->NodeText.IsEmpty)
            {
                continue;
            }

            log.Information("  node {Id}: w={Width} {Text}", id, node->GetWidth(), AtkText.ReadNodeText(node));
        }
    }
}
