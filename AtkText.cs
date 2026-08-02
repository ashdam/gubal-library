using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Reads the string out of an <see cref="AtkValue" />, or nothing if it does not hold one.
/// </summary>
/// <remarks>
///     <para>
///         <b>The type check is the whole point.</b> <see cref="AtkValue" /> is a tagged union:
///         <see cref="AtkValue.String" /> shares storage with <c>Int</c>, <c>Float</c> and
///         <c>Pointer</c>. Reading it on a value whose <see cref="AtkValue.Type" /> is anything else
///         reinterprets a number as an address and dereferences it. That crashed the game at the
///         character-select screen, where an addon refresh arrived carrying an <c>Int</c> and its
///         payload — <c>0x12345679</c> — became the pointer that faulted.
///     </para>
///     <para>
///         There is no recovering from that after the fact. An <see cref="AccessViolationException" />
///         is uncatchable on .NET Core: a <c>try</c>/<c>catch</c> around the read never runs, the
///         runtime tears the process down, and the player loses the client with no Dalamud error to
///         show for it. The guard has to come first, so it lives here rather than at each call site.
///     </para>
///     <para>
///         Shared rather than repeated because it had already been written four times and only the
///         two diagnostic copies checked the type. The two that inject were the two that crashed.
///     </para>
/// </remarks>
internal static unsafe class AtkText
{
    /// <summary>Which type tags actually carry a null-terminated byte string.</summary>
    /// <remarks>
    ///     <c>WideString</c> is deliberately absent: it holds a <c>char*</c>, so reading it as UTF-8
    ///     would not fault but would produce nonsense. Nothing in this plugin has ever seen one.
    /// </remarks>
    public static bool HoldsString(AtkValue value)
    {
        return value.Type is AtkValueType.String
            or AtkValueType.ManagedString
            or AtkValueType.ConstString;
    }

    public static string ReadString(AtkValue value)
    {
        if (!HoldsString(value))
        {
            return string.Empty;
        }

        var pointer = (nint)value.String.Value;
        return pointer == nint.Zero
            ? string.Empty
            : MemoryHelper.ReadSeStringAsString(out _, pointer);
    }

    /// <summary>
    ///     Reads a text node's on-screen string.
    /// </summary>
    /// <remarks>
    ///     The <c>catch</c> earns its place — wrapped payloads carry raw control bytes and the reader
    ///     does throw on them — but note what it is not. It is no defence against a bad pointer, for
    ///     the same reason given above. The null and empty checks in front of the read are what make
    ///     this safe; the catch only decides how to render something already known to be readable.
    ///     <para>
    ///         Shared for the same reason as <see cref="ReadString" />: three copies existed and only
    ///         one of them checked for a null node.
    ///     </para>
    /// </remarks>
    public static string ReadNodeText(AtkTextNode* node)
    {
        if (node is null || node->NodeText.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return MemoryHelper.ReadSeStringAsString(out _, (nint)node->NodeText.StringPtr.Value);
        }
        catch
        {
            return node->NodeText.ToString();
        }
    }
}
