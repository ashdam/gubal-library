using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Regrows a speech balloon around text that got longer.
/// </summary>
/// <remarks>
///     <para>
///         A balloon is four independent nodes — <c>BubbleTextNode</c>, the
///         <c>BubbleNineGridNode</c> drawn behind it, the <c>BubbleImageNode</c> tail that points at
///         the speaker, and a root. The game sizes them for the line it is about to draw, so writing a
///         longer translation into the text node alone leaves the graphic at the size the English
///         needed and the Spanish hangs outside it.
///     </para>
///     <para>
///         <b>It widens the balloon; it does not wrap the text.</b> That is the game's own model:
///         balloon text arrives with <c>MultiLine</c> and <em>not</em> <c>WordWrap</c> — measured, and
///         unlike <c>TalkSubtitle</c>, which gets <c>Edge, WordWrap, MultiLine</c> — so lines break
///         only where the text says so and the balloon is sized to the widest of them.
///     </para>
///     <para>
///         Enabling <c>WordWrap</c> was tried, and was worse than the bug it was meant to fix. A
///         wrapped node cannot grow sideways: it wraps at the width it already has, and a balloon the
///         game sized for a short line is a few dozen pixels wide, so a translation came out as a tall
///         thin column broken mid-word. It also leaked — these nodes are reused from one balloon to the
///         next and the flag stayed set, so untranslated English lines came out stretched too. Hence
///         clearing the bit here rather than merely not setting it.
///     </para>
///     <para>
///         <b>Nothing here is a measured constant.</b> The padding around the text, the top margin and
///         both anchors are read off the balloon the game has just laid out, immediately before
///         anything changes. Hard-coding a sample would break the day the game restyles a balloon or a
///         UI scale differs, and would be untestable besides. Calibrating costs five subtractions.
///     </para>
///     <para>
///         <b>Centred sideways, anchored at the bottom.</b> Measured: a 67-wide grid at x=-33.5 centres
///         on zero, and so does the 16-wide tail, while the grid's bottom edge sits exactly on the
///         anchor. So width grows from the centre — a corner would walk the balloon sideways as the
///         text lengthened — and height goes on top, since growing downwards would push the balloon
///         through the speaker's head.
///     </para>
/// </remarks>
internal static unsafe class BalloonLayout
{
    /// <summary>
    ///     Regrows the balloon around whatever text its node now holds. Safe to call on every write.
    /// </summary>
    /// <remarks>
    ///     Idempotent by construction rather than by a guard: every value written is derived from the
    ///     text currently in the node, never from the previous result, so calling it twice on the same
    ///     line computes the same geometry twice instead of compounding.
    /// </remarks>
    /// <param name="addon">The <c>_MiniTalk</c> addon. Callers must not pass any other addon.</param>
    /// <param name="node">The text node just written to, used to find which balloon owns it.</param>
    /// <returns>True if a balloon was found and refitted.</returns>
    public static bool Fit(AtkUnitBase* addon, AtkTextNode* node)
    {
        if (addon is null || node is null)
        {
            return false;
        }

        foreach (ref var bubble in ((AddonMiniTalk*)addon)->TalkBubbles)
        {
            if (bubble.BubbleTextNode != node)
            {
                continue;
            }

            Refit(node, bubble.BubbleNineGridNode);
            return true;
        }

        return false;
    }

    private static void Refit(AtkTextNode* text, AtkNineGridNode* grid)
    {
        // Calibrate before touching anything: these describe the balloon as the game built it.
        var padX = grid is null ? 0 : grid->GetWidth() - text->GetWidth();
        var padY = grid is null ? 0 : grid->GetHeight() - text->GetHeight();
        var centre = grid is null ? 0f : grid->AtkResNode.X + (grid->GetWidth() / 2f);
        var bottom = grid is null ? 0f : grid->AtkResNode.Y + grid->GetHeight();
        var topMargin = grid is null ? 0f : text->AtkResNode.Y - grid->AtkResNode.Y;

        // WordWrap OFF, deliberately, and this is the correction that matters. The game's own balloons
        // do not have it: they break lines only where the text says so and widen the balloon to fit.
        // Setting it imposed a different layout model, and a broken one — a wrapped node cannot grow
        // sideways, so it wraps at whatever width it already had. For a balloon the game sized to a
        // short line that is a few dozen pixels, which turned every translation into a tall thin
        // column and, because node state is reused between balloons, did it to untranslated lines too.
        text->TextFlags &= ~TextFlags.WordWrap;

        // Measured, not resized. ResizeNodeForCurrentText only ever grows: a node the game had sized
        // for two lines of English keeps that height when the Spanish fits on one, so a single-line
        // balloon came out with room for two — visible, and the kind of thing that reads as "the plugin
        // makes balloons look wrong" rather than as a bug with a cause.
        ushort textWidth;
        ushort textHeight;
        text->GetTextDrawSize(&textWidth, &textHeight);

        text->SetWidth(textWidth);
        text->SetHeight(textHeight);

        if (grid is null)
        {
            return;
        }

        var width = (ushort)(textWidth + padX);
        var height = (ushort)(textHeight + padY);

        // Centred horizontally on the speaker and anchored by its bottom edge, which is where the tail
        // hangs from — both read off the balloon above rather than assumed. Growing from the centre is
        // what keeps the tail pointing at the right head; growing from a corner would walk the balloon
        // sideways as the text got longer.
        //
        // SetPositionFloat rather than assigning X and Y. Writing the fields moves the numbers and
        // nothing else, because the node's draw transform is derived: the graphic would stay put while
        // claiming to have moved, and the text would slide out of it.
        grid->AtkResNode.SetWidth(width);
        grid->AtkResNode.SetHeight(height);
        grid->AtkResNode.SetPositionFloat(centre - (width / 2f), bottom - height);

        text->AtkResNode.SetPositionFloat(centre - (textWidth / 2f), grid->AtkResNode.Y + topMargin);
    }
}
