using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Walks an addon's whole node tree, descending into component nodes.
/// </summary>
/// <remarks>
///     <para>
///         Exists because <c>AtkUnitBase.GetTextNodeById</c> only reaches the addon's own top-level
///         node list. That is enough for <c>Talk</c>, <c>TalkSubtitle</c> and <c>_BattleTalk</c>, whose
///         text sits at the top — and it silently finds nothing for any addon that wraps its text in a
///         component.
///     </para>
///     <para>
///         <c>_MiniTalk</c>, the speech balloon, is the case that forced this. It carries no
///         <c>AtkValues</c> at all and puts its line in node 3 one level down, so it was invisible to
///         a value listener and to an id-based node scan at once. Three separate attempts failed
///         before the tree was walked properly.
///     </para>
/// </remarks>
internal static unsafe class AddonNodes
{
    /// <summary>Node types at or above this value are components holding their own node list.</summary>
    private const ushort ComponentTypeFloor = 1000;

    /// <summary>Guard against a malformed tree rather than a limit on real nesting.</summary>
    private const int MaxDepth = 6;

    /// <summary>
    ///     Collects every non-empty text node in the addon, as raw pointers.
    /// </summary>
    /// <remarks>
    ///     Pointers rather than an iterator because a method cannot both be unsafe-yielding and hand
    ///     back <c>AtkTextNode*</c>. The caller casts back; the list is reused across frames so the
    ///     sweep does not allocate.
    /// </remarks>
    public static void CollectTextNodes(AtkUnitBase* addon, List<nint> into)
    {
        into.Clear();

        if (addon is not null)
        {
            Walk(addon->UldManager, into, 0);
        }
    }

    private static void Walk(AtkUldManager manager, List<nint> into, int depth)
    {
        if (depth > MaxDepth || manager.NodeList is null)
        {
            return;
        }

        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var node = manager.NodeList[i];
            if (node is null)
            {
                continue;
            }

            if ((ushort)node->Type >= ComponentTypeFloor)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                {
                    Walk(component->UldManager, into, depth + 1);
                }

                continue;
            }

            if (node->Type != NodeType.Text)
            {
                continue;
            }

            if (!((AtkTextNode*)node)->NodeText.IsEmpty)
            {
                into.Add((nint)node);
            }
        }
    }
}
