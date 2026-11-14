using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glasswork;
using Glasswork.Backend;
using Glasswork.Dalamud;

namespace GubalLibrary;

internal sealed partial class PluginUi(IDalamudPluginInterface pluginInterface, IPluginLog log) : IDisposable
{
    public GlassworkHost Glasswork { get; } = CreateGlasswork(pluginInterface, log);

    private static GlassworkHost CreateGlasswork(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        var host = new GlassworkHost(pluginInterface, log)
        {
            Look = Look.NeutralDark().WithAccent(Color.FromVector4(PluginTheme.Gold)),
        };
        host.Look.ControlHeight = 36;
        host.Look.ControlHeightLarge = PluginTheme.ActionHeight;
        host.Look.MinHit = PluginTheme.CompactActionHeight;
        host.Look.ControlRadius = 5;
        host.Look.Label = Color.FromHex(0xdfe4ec);
        host.Look.SecondaryLabel = Color.FromHex(0x98a3b6);
        host.Look.Fill = Color.FromHex(0x253047);
        host.Look.SecondaryFill = Color.FromHex(0x253047, 0.15f);
        host.Look.Link = host.Look.FocusRing = Color.FromHex(0x7fc8ef);
        host.Look.Type.Set(TextRole.Body, UiBuilder.DefaultFontSizePx, FontWeight.Regular);
        host.Look.Type.Set(TextRole.Callout, UiBuilder.DefaultFontSizePx + 2, FontWeight.Semibold);
        host.Look.Type.Set(TextRole.Headline, UiBuilder.DefaultFontSizePx + 2, FontWeight.Semibold);
        host.Inspector.Hotkey = ImGuiKey.None;
        return host;
    }

    public IFontHandle ActionFont { get; } = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(step =>
        step.OnPreBuild(toolkit => toolkit.AddDalamudDefaultFont(UiBuilder.DefaultFontSizePx + 2)));

    private static string HeadingFontPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "georgia.ttf");

    public IFontHandle HeadingFont { get; } = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(step =>
        step.OnPreBuild(toolkit =>
        {
            if (File.Exists(HeadingFontPath))
                toolkit.AddFontFromFile(HeadingFontPath, new SafeFontConfig { SizePx = UiBuilder.DefaultFontSizePx + 9 });
            else toolkit.AddDalamudDefaultFont(UiBuilder.DefaultFontSizePx + 9);
        }));

    public void DrawHeading(string text)
    {
        using var font = this.HeadingFont.Push();
        using var color = ImRaii.PushColor(ImGuiCol.Text, PluginTheme.Gold);
        ImGui.TextUnformatted(text);
    }

    public IFontHandle NavigationFont { get; } = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(step =>
        step.OnPreBuild(toolkit => toolkit.AddDalamudDefaultFont(UiBuilder.DefaultFontSizePx)));

    public bool DrawActionButton(string label, bool primary, bool compact = false, string? detail = null, FontAwesomeIcon? icon = null)
    {
        var size = this.MeasureActionButton(label, compact, detail, icon);
        var clicked = Gw.Button(detail is null ? label : "##" + label,
            primary ? ButtonStyle.Prominent : ButtonStyle.Default,
            compact ? ButtonSize.Regular : ButtonSize.Large,
            icon: detail is null ? icon?.ToIconString() ?? string.Empty : string.Empty, width: size.X / Frame.Scale);
        if (detail is not null)
        {
            var titleStyle = new TextStyle(TextRole.Headline);
            var detailStyle = new TextStyle(TextRole.Caption);
            var titleSize = TextPaint.Measure(label, titleStyle, Frame.Look, Frame.Scale);
            var detailSize = TextPaint.Measure(detail, detailStyle, Frame.Look, Frame.Scale);
            var iconWidth = icon is null ? 0 : TextPaint.Measure(icon.Value.ToIconString(), new TextStyle(TextRole.Icon), Frame.Look, Frame.Scale).X;
            var gap = icon is null ? 0 : Frame.Look.SpaceSmall * Frame.Scale;
            var position = ImGui.GetItemRectMin();
            var top = position.Y + (size.Y - titleSize.Y - detailSize.Y) / 2;
            var ink = (primary ? Frame.Look.OnAccent : Frame.Look.Label).MultiplyAlpha(ImGui.GetStyle().Alpha);
            var titleLeft = position.X + (size.X - titleSize.X - iconWidth - gap) / 2;
            if (icon is not null)
                TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(titleLeft, top), icon.Value.ToIconString(),
                    new TextStyle(TextRole.Icon), ink, Frame.Look, Frame.Scale);
            TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(titleLeft + iconWidth + gap, top),
                label, titleStyle, ink, Frame.Look, Frame.Scale);
            TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(position.X + (size.X - detailSize.X) / 2, top + titleSize.Y),
                detail, detailStyle, ink, Frame.Look, Frame.Scale);
        }
        return clicked;
    }

    public Vector2 MeasureActionButton(string label, bool compact = false, string? detail = null, FontAwesomeIcon? icon = null)
    {
        var text = TextPaint.Measure(label, new TextStyle(TextRole.Headline), Frame.Look, Frame.Scale);
        var titleWidth = text.X + (icon is null ? 0 : TextPaint.Measure(icon.Value.ToIconString(), new TextStyle(TextRole.Icon), Frame.Look, Frame.Scale).X
            + Frame.Look.SpaceSmall * Frame.Scale);
        var width = detail is null ? titleWidth : Math.Max(titleWidth,
            TextPaint.Measure(detail, new TextStyle(TextRole.Caption), Frame.Look, Frame.Scale).X);
        return new Vector2(MathF.Ceiling(width + (Frame.Look.SpaceLarge * 2 + 2) * Frame.Scale),
            (compact ? PluginTheme.CompactActionHeight : PluginTheme.ActionHeight) * Frame.Scale);
    }

    public void DrawCapturePlaceholder(Vector2 position, Vector2 size, string label, string shortcut)
    {
        var draw = ImGui.GetWindowDrawList();
        var icon = FontAwesomeIcon.Expand.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont)) iconSize = ImGui.CalcTextSize(icon);
        Vector2 labelSize;
        using (this.ActionFont.Push()) labelSize = ImGui.CalcTextSize(label);
        var shortcutSize = ImGui.CalcTextSize(shortcut);
        var gap = 12f * ImGuiHelpers.GlobalScale;
        var top = position.Y + (size.Y - iconSize.Y - labelSize.Y - shortcutSize.Y - gap * 2) / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            draw.AddText(new Vector2(position.X + (size.X - iconSize.X) / 2, top), ImGui.GetColorU32(PluginTheme.Gold), icon);
        var labelPosition = new Vector2(position.X + (size.X - labelSize.X) / 2, top + iconSize.Y + gap);
        using (this.ActionFont.Push())
        {
            draw.AddText(labelPosition, ImGui.GetColorU32(ImGuiCol.Text), label);
            EmphasizeText(labelPosition, ImGui.GetColorU32(ImGuiCol.Text), label);
        }
        draw.AddText(new Vector2(position.X + (size.X - shortcutSize.X) / 2, labelPosition.Y + labelSize.Y + gap),
            ImGui.GetColorU32(PluginTheme.Muted), shortcut);
    }

    public static void EmphasizeText(Vector2 position, uint color, string label) =>
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            position + new Vector2(0.65f * ImGuiHelpers.GlobalScale, 0), color, label);

    public static void DrawRequiredLabel(string label)
    {
        DrawSubheading(label);
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextColored(new Vector4(0.94f, 0.3f, 0.3f, 1), "*");
    }

    public void Dispose()
    {
        this.Glasswork.Dispose();
        this.ActionFont.Dispose();
        this.HeadingFont.Dispose();
        this.NavigationFont.Dispose();
    }
}
