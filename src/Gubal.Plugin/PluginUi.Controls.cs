using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Glasswork;
using Glasswork.Backend;

namespace GubalLibrary;

internal sealed partial class PluginUi
{
    private readonly struct ControlScope : IDisposable
    {
        private readonly Look look;
        private readonly float height;
        private readonly float smallHeight;
        private readonly float minHit;
        private readonly Vector2 switchSize;

        public ControlScope(float pixelHeight)
        {
            this.look = Frame.Look;
            this.height = this.look.ControlHeight;
            this.smallHeight = this.look.ControlHeightSmall;
            this.minHit = this.look.MinHit;
            this.switchSize = this.look.SwitchSize;
            var logicalHeight = pixelHeight / Frame.Scale;
            this.look.ControlHeight = logicalHeight;
            this.look.ControlHeightSmall = logicalHeight;
            this.look.MinHit = Math.Min(this.minHit, logicalHeight);
            this.look.SwitchSize = new Vector2(logicalHeight * 1.8f, logicalHeight);
        }

        public void Dispose()
        {
            this.look.ControlHeight = this.height;
            this.look.ControlHeightSmall = this.smallHeight;
            this.look.MinHit = this.minHit;
            this.look.SwitchSize = this.switchSize;
        }
    }

    public static bool Button(string label, bool small = false)
    {
        using var controls = new ControlScope(ImGui.GetFrameHeight());
        return Gw.Button(label, size: small ? ButtonSize.Small : ButtonSize.Regular);
    }

    public static Vector2 MeasureButton(string label, bool small = false) => new(
        MathF.Ceiling(TextPaint.Measure(label, small ? new TextStyle(TextRole.Footnote, FontWeight.Semibold)
            : new TextStyle(TextRole.Headline), Frame.Look, Frame.Scale).X
            + ((small ? Frame.Look.SpaceMedium : Frame.Look.SpaceLarge) * 2 + 2) * Frame.Scale), ImGui.GetFrameHeight());

    public static bool Checkbox(string label, ref bool value)
    {
        using var controls = new ControlScope(ImGui.GetFrameHeight());
        return Gw.Checkbox(ref value, label);
    }

    public static bool Toggle(string id, ref bool value)
    {
        using var controls = new ControlScope(ImGui.GetFrameHeight());
        return Gw.Toggle(ref value, id);
    }

    public static bool TextField(string id, ref string text, int maxLength)
    {
        using var scope = ImRaii.PushId(id);
        using var controls = new ControlScope(ImGui.GetFrameHeight());
        var marker = id.IndexOf("##", StringComparison.Ordinal);
        var label = marker < 0 ? id : id[..marker];
        return Gw.TextField(ref text, label: label, maxLength: maxLength);
    }

    public static bool TextArea(string id, ref string text, int maxLength, Vector2 size)
    {
        using var scope = ImRaii.PushId(id);
        var width = size.X > 0 ? size.X : Math.Max(1, ImGui.GetContentRegionAvail().X + size.X);
        ImGui.SetNextItemWidth(width);
        var padding = Frame.Look.SpaceSmall * Frame.Scale * 2;
        var lineHeight = Frame.Look.Type.LineHeight(TextRole.Body) * Frame.Scale;
        var lines = Math.Max(1, (int)Math.Floor((size.Y - padding) / lineHeight));
        return Gw.TextArea(ref text, lines: lines, maxLength: maxLength);
    }

    public static bool ComboBox(string id, ref int selected, ReadOnlySpan<string> items)
    {
        using var scope = ImRaii.PushId(id);
        using var controls = new ControlScope(ImGui.GetFrameHeight());
        ImGui.SetNextItemWidth(Math.Max(ImGui.CalcItemWidth(), ComboBoxWidth(items)));
        return Gw.ComboBox(ref selected, items);
    }

    public static float ComboBoxWidth(ReadOnlySpan<string> items)
    {
        var width = 0f;
        foreach (var item in items)
            width = Math.Max(width, TextPaint.Measure(item, new TextStyle(TextRole.Body), Frame.Look, Frame.Scale).X);
        return width + (Frame.Look.SpaceLarge * 2 + Frame.Look.SpaceMedium * 2 + Frame.Look.SpaceSmall * 2 + 36)
            * Frame.Scale + ImGui.GetStyle().ScrollbarSize;
    }

    public static bool IconButton(string id, FontAwesomeIcon icon, bool selected = false, bool compact = true, string? tooltip = null)
    {
        using var scope = ImRaii.PushId(id);
        return Gw.IconButton(icon.ToIconString(), selected ? ButtonStyle.Prominent : ButtonStyle.Default,
            compact ? ButtonSize.Regular : ButtonSize.Large, tooltip ?? string.Empty,
            selected ? Frame.Look.OnAccent : null);
    }

    public static bool SmallIconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        using var controls = new ControlScope(ImGui.GetFrameHeight());
        return IconButton(id, icon, tooltip: tooltip);
    }

    public static void ProgressBar(float fraction, Vector2 size, string overlay = "")
    {
        var height = Frame.Look.SliderTrackHeight;
        try
        {
            Frame.Look.SliderTrackHeight = size.Y / Frame.Scale;
            var width = size.X > 0 ? size.X : Math.Max(1, ImGui.GetContentRegionAvail().X + size.X);
            var value = fraction < 0 ? (1f + MathF.Sin((float)ImGui.GetTime() * 2)) / 2 : fraction;
            Gw.ProgressBar(value, width / Frame.Scale);
            if (overlay.Length == 0) return;
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var fillX = min.X + (max.X - min.X) * Math.Clamp(value, 0f, 1f);
            var textPosition = (min + max - ImGui.CalcTextSize(overlay)) / 2;
            var draw = ImGui.GetWindowDrawList();
            if (fillX > min.X)
            {
                draw.PushClipRect(min, new Vector2(fillX, max.Y), true);
                draw.AddText(textPosition, Frame.Look.OnAccent.MultiplyAlpha(ImGui.GetStyle().Alpha).ToAbgr(), overlay);
                draw.PopClipRect();
            }
            if (fillX < max.X)
            {
                draw.PushClipRect(new Vector2(fillX, min.Y), max, true);
                draw.AddText(textPosition, ImGui.GetColorU32(ImGuiCol.Text), overlay);
                draw.PopClipRect();
            }
        }
        finally { Frame.Look.SliderTrackHeight = height; }
    }

    public static bool Selectable(string label, bool selected, Vector2 size)
    {
        var position = ImGui.GetCursorScreenPos();
        var width = size.X > 0 ? size.X : ImGui.GetContentRegionAvail().X;
        var height = size.Y > 0 ? size.Y : ImGui.GetTextLineHeight();
        using var scope = ImRaii.PushId(label);
        using var controls = new ControlScope(height);
        var clicked = Gw.Button("##select", selected ? ButtonStyle.Tinted : ButtonStyle.Plain, width: width / Frame.Scale);
        var visible = label.AsSpan();
        var marker = visible.IndexOf("##");
        if (marker >= 0) visible = visible[..marker];
        if (!visible.IsEmpty)
        {
            var text = visible.ToString();
            var textSize = ImGui.CalcTextSize(text);
            ImGui.GetWindowDrawList().AddText(position + new Vector2(ImGui.GetStyle().FramePadding.X, (height - textSize.Y) / 2),
                ImGui.GetColorU32(ImGuiCol.Text), text);
        }
        return clicked;
    }

    public static bool Disclosure(string label, bool? nextOpen = null)
    {
        var id = ImGui.GetID(label);
        var storage = ImGui.GetStateStorage();
        var open = nextOpen ?? storage.GetBool(id);
        var position = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Math.Max(ImGui.GetFrameHeight(), SubheadingSize(label).Y);
        using (ImRaii.PushId(label))
        using (new ControlScope(height))
            if (Gw.Button("##disclosure", ButtonStyle.Tinted, width: width / Frame.Scale)) open = !open;
        var draw = ImGui.GetWindowDrawList();
        using (ImRaii.PushFont(UiBuilder.IconFont))
            draw.AddText(position + new Vector2(4 * Frame.Scale, (height - ImGui.GetFontSize()) / 2),
                Frame.Look.SecondaryLabel.ToAbgr(), (open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight).ToIconString());
        var marker = label.IndexOf("##", StringComparison.Ordinal);
        var visible = marker < 0 ? label : label[..marker];
        var textSize = SubheadingSize(visible);
        TextPaint.Draw(draw, position + new Vector2(24 * Frame.Scale, (height - textSize.Y) / 2), visible,
            new TextStyle(TextRole.Headline), Frame.Look.Label, Frame.Look, Frame.Scale);
        storage.SetBool(id, open);
        return open;
    }
}
