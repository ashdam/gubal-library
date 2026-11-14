using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glasswork;
using Glasswork.Backend;

namespace GubalLibrary;

internal sealed partial class PluginUi
{
    public static Vector2 SubheadingSize(string text) =>
        TextPaint.Measure(text, new TextStyle(TextRole.Headline), Frame.Look, Frame.Scale);

    public static void DrawSubheading(string text) => Gw.TextWrapped(text, TextRole.Headline);

    public static void DrawSubheading(string text, string trailing, Vector4 trailingColor)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var titleSize = SubheadingSize(text);
        var statusSize = TextPaint.Measure(trailing, new TextStyle(TextRole.Body), Frame.Look, Frame.Scale);
        DrawSubheading(text);
        var inline = titleSize.X + statusSize.X + ImGui.GetStyle().ItemSpacing.X <= width;
        var top = inline ? start.Y : ImGui.GetCursorScreenPos().Y;
        var height = inline ? Math.Max(titleSize.Y, statusSize.Y) : statusSize.Y;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - statusSize.X, top + (height - statusSize.Y) / 2));
        Gw.Text(trailing, TextRole.Body, Color.FromVector4(trailingColor));
        ImGui.SetCursorScreenPos(new Vector2(start.X, top + height + ImGui.GetStyle().ItemSpacing.Y));
    }

    public static unsafe (float Top, float Bottom) TextInkBounds(ReadOnlySpan<char> text)
    {
        var font = ImGui.GetFont();
        var scale = ImGui.GetFontSize() / font.FontSize;
        var top = float.PositiveInfinity;
        var bottom = float.NegativeInfinity;
        foreach (var character in text)
        {
            var glyph = font.FindGlyph(character);
            if (glyph is null || glyph->X1 <= glyph->X0 || glyph->Y1 <= glyph->Y0) continue;
            top = Math.Min(top, glyph->Y0 * scale);
            bottom = Math.Max(bottom, glyph->Y1 * scale);
        }
        return float.IsFinite(top) ? (top, bottom) : (0, ImGui.GetFontSize());
    }
}
