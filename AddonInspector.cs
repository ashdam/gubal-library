using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    /// <summary>Node types at or above this value are components holding their own node list.</summary>
    private const ushort ComponentTypeFloor = 1000;


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

    /// <summary>
    ///     Describes every text node found by walking the tree, with the geometry that decides layout.
    /// </summary>
    /// <remarks>
    ///     <see cref="Dump" /> cannot see these. It asks <c>GetTextNodeById</c>, which only reaches the
    ///     addon's own top-level list, so for any addon that wraps its text in a component it reports
    ///     nothing at all — which is why no balloon was ever described despite the inspector existing.
    ///     This takes the list <see cref="AddonNodes.CollectTextNodes" /> already built.
    ///     <para>
    ///         Width alone is not enough to reason about overflow. The flags decide whether a line-break
    ///         payload breaks anything and whether long text wraps or runs off the edge; the font size
    ///         and line spacing decide how much room the text needs. Printing only width is what left
    ///         the balloon overflow undiagnosable from a log.
    ///     </para>
    /// </remarks>
    public static void DumpTextNodes(IPluginLog log, string addonName, string when, List<nint> textNodes)
    {
        log.Information("=== {Addon} ({When}): {Count} text node(s) ===", addonName, when, textNodes.Count);

        foreach (var pointer in textNodes)
        {
            var node = (AtkTextNode*)pointer;

            log.Information(
                "  node {Id}: {Width}x{Height} font={Font} flags={Flags} spacing={Spacing} align={Align} text={Text}",
                node->NodeId,
                node->GetWidth(),
                node->GetHeight(),
                node->FontSize,
                node->TextFlags,
                node->LineSpacing,
                node->AlignmentType,
                AtkText.ReadNodeText(node));
        }
    }

    /// <summary>
    ///     Describes <em>every</em> node of an addon, not only the ones holding text.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The text nodes were never the whole story. Growing a text node to fit a longer
    ///         translation only helps if whatever is drawn behind it grows too, and for a battle-talk
    ///         banner nothing here knew whether that background is a fixed graphic or a nine-grid the
    ///         game resizes per line. That question cannot be answered from the text nodes alone, and
    ///         answering it by trying was what cost three rounds on the balloons.
    ///     </para>
    ///     <para>
    ///         Invisible nodes are reported too, marked, because a pooled or alternate layout that is
    ///         currently hidden is exactly the thing a walk that skips them would hide.
    ///     </para>
    /// </remarks>
    public static void DumpAllNodes(IPluginLog log, string addonName, AtkUnitBase* addon, string when = "full tree")
    {
        if (addon is null)
        {
            return;
        }

        log.Information("=== {Addon} ({When}) ===", addonName, when);
        WalkForDump(log, addon->UldManager, 0);
    }

    private static void WalkForDump(IPluginLog log, AtkUldManager manager, int depth)
    {
        if (depth > 6 || manager.NodeList is null)
        {
            return;
        }

        var indent = new string(' ', 2 + (depth * 2));

        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var node = manager.NodeList[i];
            if (node is null)
            {
                continue;
            }

            var hidden = (node->NodeFlags & NodeFlags.Visible) == 0 ? " HIDDEN" : string.Empty;
            var text = node->Type == NodeType.Text
                ? " text=" + AtkText.ReadNodeText((AtkTextNode*)node)
                : string.Empty;

            log.Information(
                "{Indent}node {Id} {Type}: {Width}x{Height} at ({X},{Y}){Hidden}{Text}",
                indent,
                node->NodeId,
                node->Type,
                node->GetWidth(),
                node->GetHeight(),
                node->X,
                node->Y,
                hidden,
                text);

            if ((ushort)node->Type >= ComponentTypeFloor)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                {
                    WalkForDump(log, component->UldManager, depth + 1);
                }
            }
        }
    }

    /// <summary>
    ///     Describes a speech balloon's nodes, which are sized independently of one another.
    /// </summary>
    /// <remarks>
    ///     <c>AddonMiniTalk.TalkBubbleEntry</c> gives the layout authoritatively: the line lives in
    ///     <c>BubbleTextNode</c>, the balloon graphic behind it is a separate
    ///     <c>BubbleNineGridNode</c>, and the tail that points at the NPC is a third
    ///     <c>BubbleImageNode</c>. Writing longer text into the first grows nothing else, which is the
    ///     shape of the reported overflow — so all four sizes have to be on the record before anything
    ///     is resized, or the fix is guesswork that can leave balloons detached from their speaker.
    /// </remarks>
    public static void DumpMiniTalk(IPluginLog log, AtkUnitBase* addon, string when)
    {
        if (addon is null)
        {
            return;
        }

        var mini = (AddonMiniTalk*)addon;
        var index = 0;

        foreach (ref var bubble in mini->TalkBubbles)
        {
            var current = index++;
            var text = bubble.BubbleTextNode;

            // Empty bubbles are the pool's unused slots, not information.
            if (text is null || text->NodeText.IsEmpty)
            {
                continue;
            }

            log.Information("  --- bubble {Index} ({When}) ---", current, when);
            log.Information(
                "    text     {Width}x{Height} at ({X},{Y}) font={Font} flags={Flags} spacing={Spacing} text={Text}",
                text->GetWidth(),
                text->GetHeight(),
                text->AtkResNode.X,
                text->AtkResNode.Y,
                text->FontSize,
                text->TextFlags,
                text->LineSpacing,
                AtkText.ReadNodeText(text));

            if (bubble.BubbleNineGridNode is not null)
            {
                var grid = bubble.BubbleNineGridNode;
                log.Information(
                    "    ninegrid {Width}x{Height} at ({X},{Y}) insets t={Top} b={Bottom} l={Left} r={Right}",
                    grid->GetWidth(),
                    grid->GetHeight(),
                    grid->AtkResNode.X,
                    grid->AtkResNode.Y,
                    grid->TopOffset,
                    grid->BottomOffset,
                    grid->LeftOffset,
                    grid->RightOffset);
            }

            if (bubble.BubbleImageNode is not null)
            {
                var image = bubble.BubbleImageNode;
                log.Information(
                    "    tail     {Width}x{Height} at ({X},{Y})",
                    image->GetWidth(),
                    image->GetHeight(),
                    image->AtkResNode.X,
                    image->AtkResNode.Y);
            }

            if (bubble.BubbleResNode is not null)
            {
                var res = bubble.BubbleResNode;
                log.Information(
                    "    root     {Width}x{Height} at ({X},{Y})",
                    res->GetWidth(),
                    res->GetHeight(),
                    res->X,
                    res->Y);
            }
        }
    }
}
