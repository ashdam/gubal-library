using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private IFontHandle? packActionFont;

    public void ConfigureActionFont(IUiBuilder builder) =>
        this.packActionFont = builder.FontAtlas.NewDelegateFontHandle(step =>
            step.OnPreBuild(toolkit => toolkit.AddDalamudDefaultFont(UiBuilder.DefaultFontSizePx + 2)));

    public void DisposeActionFont() => this.packActionFont?.Dispose();

    private static readonly Vector4 ThemeInk = ThemeColor(0x05070d);
    private static readonly Vector4 ThemeGold = ThemeColor(0xd8b45c);
    private static readonly (ImGuiCol Slot, Vector4 Color)[] ThemeColors =
    [
        (ImGuiCol.Text, ThemeColor(0xdfe4ec)),
        (ImGuiCol.TextDisabled, ThemeColor(0x98a3b6)),
        (ImGuiCol.WindowBg, ThemeColor(0x0a0e18)),
        (ImGuiCol.ChildBg, ThemeColor(0x0e1422)),
        (ImGuiCol.PopupBg, ThemeColor(0x0a0e18)),
        (ImGuiCol.Border, ThemeColor(0xd8b45c, 0.3f)),
        (ImGuiCol.TitleBg, ThemeColor(0x0e1422)),
        (ImGuiCol.TitleBgActive, ThemeColor(0x0e1422)),
        (ImGuiCol.TitleBgCollapsed, ThemeColor(0x0a0e18)),
        (ImGuiCol.FrameBg, ThemeColor(0x151e30)),
        (ImGuiCol.FrameBgHovered, ThemeColor(0x213047)),
        (ImGuiCol.FrameBgActive, ThemeColor(0x2b3c54)),
        (ImGuiCol.Button, ThemeColor(0x253047)),
        (ImGuiCol.ButtonHovered, ThemeColor(0x3d4b5c)),
        (ImGuiCol.ButtonActive, ThemeColor(0x546173)),
        (ImGuiCol.Header, ThemeColor(0xd8b45c, 0.15f)),
        (ImGuiCol.HeaderHovered, ThemeColor(0xd8b45c, 0.24f)),
        (ImGuiCol.HeaderActive, ThemeColor(0xd8b45c, 0.32f)),
        (ImGuiCol.Tab, ThemeColor(0x0e1422)),
        (ImGuiCol.TabHovered, ThemeColor(0xd8b45c, 0.24f)),
        (ImGuiCol.TabActive, ThemeColor(0xd8b45c, 0.16f)),
        (ImGuiCol.TabUnfocused, ThemeColor(0x0a0e18)),
        (ImGuiCol.TabUnfocusedActive, ThemeColor(0x1a2231)),
        (ImGuiCol.CheckMark, ThemeColor(0xd8b45c)),
        (ImGuiCol.SliderGrab, ThemeColor(0xd8b45c)),
        (ImGuiCol.SliderGrabActive, ThemeColor(0xf2d99b)),
        (ImGuiCol.PlotHistogram, ThemeColor(0xd8b45c)),
        (ImGuiCol.PlotHistogramHovered, ThemeColor(0xf2d99b)),
        (ImGuiCol.Separator, ThemeColor(0xd8b45c, 0.22f)),
        (ImGuiCol.SeparatorHovered, ThemeColor(0xd8b45c, 0.55f)),
        (ImGuiCol.SeparatorActive, ThemeColor(0xd8b45c)),
        (ImGuiCol.ResizeGrip, ThemeColor(0xd8b45c, 0.15f)),
        (ImGuiCol.ResizeGripHovered, ThemeColor(0xd8b45c, 0.45f)),
        (ImGuiCol.ResizeGripActive, ThemeColor(0xd8b45c, 0.7f)),
        (ImGuiCol.ScrollbarBg, ThemeColor(0x05070d)),
        (ImGuiCol.ScrollbarGrab, ThemeColor(0x253047)),
        (ImGuiCol.ScrollbarGrabHovered, ThemeColor(0x3d6f8d)),
        (ImGuiCol.ScrollbarGrabActive, ThemeColor(0x7fc8ef)),
        (ImGuiCol.TextSelectedBg, ThemeColor(0x7fc8ef, 0.25f)),
        (ImGuiCol.NavHighlight, ThemeColor(0x7fc8ef)),
        (ImGuiCol.TableHeaderBg, ThemeColor(0x151e30)),
        (ImGuiCol.TableBorderStrong, ThemeColor(0xd8b45c, 0.22f)),
        (ImGuiCol.TableBorderLight, ThemeColor(0xdfe4ec, 0.08f)),
    ];

    public override void PreDraw()
    {
        base.PreDraw();
        this.UpdateInitialPackSize();
        // Apply the palette before Begin and restore it after End.
        foreach (var (slot, color) in ThemeColors) ImGui.PushStyleColor(slot, color);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(ThemeColors.Length);
        base.PostDraw();
    }

    private static Vector4 ThemeColor(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

    private bool DrawPackActionButton(string label)
    {
        using var font = this.packActionFont?.Push();
        using var text = ImRaii.PushColor(ImGuiCol.Text, ThemeInk);
        using var background = ImRaii.PushColor(ImGuiCol.Button, ThemeGold);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, ThemeColor(0xf2d99b));
        using var pressed = ImRaii.PushColor(ImGuiCol.ButtonActive, ThemeColor(0xb8944a));
        using var alignment = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f));
        var size = PackActionButtonSize(label);
        var padding = (size - ImGui.CalcTextSize(label)) / 2;
        using var framePadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, padding);
        var clicked = ImGui.Button(label, size);
        EmphasizeText(ImGui.GetItemRectMin() + padding, ImGui.GetColorU32(ThemeInk), label);
        return clicked;
    }

    private Vector2 PackActionButtonSize(string label)
    {
        using var font = this.packActionFont?.Push();
        var height = PackFooterSize * ImGuiHelpers.GlobalScale;
        var text = ImGui.CalcTextSize(label);
        var padding = Math.Max(10f * ImGuiHelpers.GlobalScale, (height - text.Y) / 2);
        return new Vector2(text.X + padding * 2, height);
    }

    private void DrawPackActiveBadge()
    {
        using var font = this.packActionFont?.Push();
        var label = Loc.Localize("Setup.Active", "Active");
        var size = this.PackActionButtonSize(label);
        this.AlignPackAction(label, button: true);
        var position = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(Green),
            5f * ImGuiHelpers.GlobalScale);
        draw.AddText(position + (size - ImGui.CalcTextSize(label)) / 2,
            ImGui.GetColorU32(ThemeInk), label);
        EmphasizeText(position + (size - ImGui.CalcTextSize(label)) / 2,
            ImGui.GetColorU32(ThemeInk), label);
        ImGui.Dummy(size);
    }

    private static void EmphasizeText(Vector2 position, uint color, string label) =>
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            position + new Vector2(0.65f * ImGuiHelpers.GlobalScale, 0), color, label);
}
