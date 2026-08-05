using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Grows a reflowing overlay — and the panel drawn behind it — to the height its text now needs.
/// </summary>
/// <remarks>
///     <para>
///         The counterpart to <see cref="BalloonLayout" />, and the two are opposites on purpose.
///         Which applies is written on the text node. A balloon has no <c>WordWrap</c>: the game breaks
///         its lines only where the text says so and widens the balloon, so a longer translation needs
///         width. A banner like <c>_BattleTalk</c> has <c>WordWrap</c> and a fixed column, so the game
///         reflows the line and what it needs is height.
///     </para>
///     <para>
///         <b>The text node was never enough.</b> Growing it alone left the extra line drawn below the
///         panel: measured on <c>_BattleTalk</c>, the body sat at y=24 and grew to 46 — ending at 70 —
///         while the <c>NineGrid</c> behind it ran 12 to 62. Eight pixels of Spanish outside the
///         graphic. Reporting that as fixed on the strength of the node's height alone was a real
///         mistake: "the node fits" and "it looks right" are different claims.
///     </para>
///     <para>
///         The panel is resizable, which is what makes this a fix rather than a choice between overflow
///         and a smaller font: <c>_BattleTalk</c> node 7 is a <c>NineGrid</c>, and the game sizes it per
///         line count — its own English lines run to three.
///     </para>
///     <para>
///         <b>Why this remembers the panel's padding instead of growing by a delta.</b> Growing both
///         nodes by the amount the text grew looks self-correcting and is not: the game resets the text
///         node's height for each line but leaves the panel alone, so the delta is recomputed from
///         scratch every time and lands on a panel that already carries the last one. Measured: a panel
///         that should have read 72 read 94, which is 50 + 22 + 22. Two passes. Capturing the padding
///         once and then always setting an absolute height cannot accumulate, and self-corrects a panel
///         that is already wrong.
///     </para>
/// </remarks>
internal sealed unsafe class OverlayLayout
{
    /// <summary>Node types at or above this value are components holding their own node list.</summary>
    private const ushort ComponentTypeFloor = 1000;

    /// <summary>
    ///     The chrome around the body text, per panel node: the panel's height minus the text's.
    /// </summary>
    /// <remarks>
    ///     Captured the first time a panel is seen, when both heights are still the game's own, and
    ///     reused from then on. Keyed by node pointer and never dereferenced, only looked up, so a
    ///     pointer the game later reuses costs one stale padding rather than a crash — the same
    ///     reasoning as the guards in <see cref="OverlayHandler" />.
    /// </remarks>
    private readonly Dictionary<nint, int> padding = [];

    /// <summary>
    ///     Fits a wrapping text node and its backdrop. Safe to call on every write.
    /// </summary>
    /// <param name="addon">The addon the node belongs to, for finding the panel behind it.</param>
    /// <param name="node">The text node just written to.</param>
    /// <summary>
    ///     Learns the panel's chrome from the game's own layout. Call before writing the translation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Only from a settled state, and that test is the whole method.</b> Capturing "the first
    ///         time the panel is seen" measured 52 where the real chrome is 26: the first sighting had
    ///         the text node at its ULD default height of 20 against a panel of 72, because the game had
    ///         not sized the node yet. Nothing about that pair described the layout, and a padding twice
    ///         too large put a blank line under every banner thereafter.
    ///     </para>
    ///     <para>
    ///         So a sample counts only when the node's height matches the text it is actually holding.
    ///         That rejects the unsettled first frame, and it rejects the mirror case the log also shows
    ///         — a panel reset to 50 while the node still carried a three-line 68 from the previous
    ///         line, which as a subtraction yields a negative chrome.
    ///     </para>
    ///     <para>
    ///         The smallest qualifying sample wins. Every way this can be wrong inflates the panel, so
    ///         the minimum converges on the game's own value and one bad early reading cannot stick.
    ///     </para>
    /// </remarks>
    public void Learn(AtkUnitBase* addon, AtkTextNode* node)
    {
        if (node is null || (node->TextFlags & TextFlags.WordWrap) == 0)
        {
            return;
        }

        var panel = FindBackdrop(addon, node);
        if (panel is null)
        {
            return;
        }

        ushort textWidth;
        ushort textHeight;
        node->GetTextDrawSize(&textWidth, &textHeight);

        if (textHeight == 0 || textHeight != node->GetHeight())
        {
            return;
        }

        var chrome = panel->GetHeight() - textHeight;
        if (chrome <= 0)
        {
            return;
        }

        var key = (nint)panel;
        if (!this.padding.TryGetValue(key, out var known) || chrome < known)
        {
            this.padding[key] = chrome;
        }
    }

    /// <summary>
    ///     Fits a wrapping text node and its backdrop. Safe to call on every write.
    /// </summary>
    /// <param name="addon">The addon the node belongs to, for finding the panel behind it.</param>
    /// <param name="node">The text node just written to.</param>
    public void Fit(AtkUnitBase* addon, AtkTextNode* node)
    {
        // A node that does not reflow says nothing with its height about the room the text needs, so
        // there is nothing to derive and changing it would be guessing.
        if (node is null || (node->TextFlags & TextFlags.WordWrap) == 0)
        {
            return;
        }

        var backdrop = FindBackdrop(addon, node);

        ushort width;
        ushort height;
        node->GetTextDrawSize(&width, &height);

        // Measured rather than resized: ResizeNodeForCurrentText only ever grows, so it would leave a
        // two-line height behind on the next one-line label.
        if (height == 0)
        {
            return;
        }

        if (height != node->GetHeight())
        {
            node->SetHeight(height);
        }

        // No sample yet means no idea what this panel's chrome is. Leaving it alone shows a line
        // clipped at the edge of a correctly sized panel; guessing showed every banner carrying a
        // blank line, which is worse and affects lines that were never too long in the first place.
        if (backdrop is null || !this.padding.TryGetValue((nint)backdrop, out var chrome))
        {
            return;
        }

        var wanted = (ushort)(height + chrome);
        if (wanted != backdrop->GetHeight())
        {
            backdrop->AtkResNode.SetHeight(wanted);
        }
    }

    /// <summary>
    ///     Finds the panel drawn behind a body text node: the visible nine-grid wide enough to hold it.
    /// </summary>
    /// <remarks>
    ///     By shape rather than by node id. The id would work — <c>_BattleTalk</c>'s panel is node 7 —
    ///     but this addon is not in FFXIVClientStructs at all, so an id is an observation about one
    ///     addon while this rule reads across any overlay built the same way. The width test is what
    ///     rejects the speaker-name plate, also a nine-grid but far narrower, and the visibility test
    ///     rejects the alternate layouts sitting hidden in the same tree.
    /// </remarks>
    private static AtkNineGridNode* FindBackdrop(AtkUnitBase* addon, AtkTextNode* node)
    {
        if (addon is null)
        {
            return null;
        }

        var manager = addon->UldManager;
        if (manager.NodeList is null)
        {
            return null;
        }

        var wanted = node->GetWidth();

        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var candidate = manager.NodeList[i];
            if (candidate is null || (ushort)candidate->Type >= ComponentTypeFloor)
            {
                continue;
            }

            if (candidate->Type != NodeType.NineGrid)
            {
                continue;
            }

            if ((candidate->NodeFlags & NodeFlags.Visible) == 0 || candidate->GetWidth() < wanted)
            {
                continue;
            }

            return (AtkNineGridNode*)candidate;
        }

        return null;
    }
}
