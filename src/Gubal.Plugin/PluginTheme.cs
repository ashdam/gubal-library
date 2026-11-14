using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal static class PluginTheme
{
    public static readonly Vector4 Gold = Color(0xd8b45c);
    public static readonly Vector4 Muted = Color(0x98a3b6);

    public const float ActionHeight = 50f;
    public const float CompactActionHeight = 36f;

    private static readonly (ImGuiCol Slot, Vector4 Color)[] Colors =
    [
        (ImGuiCol.Text, Color(0xdfe4ec)),
        (ImGuiCol.TextDisabled, Color(0x98a3b6)),
        (ImGuiCol.WindowBg, Color(0x0a0e18)),
        (ImGuiCol.ChildBg, Color(0x0e1422)),
        (ImGuiCol.PopupBg, Color(0x0a0e18)),
        (ImGuiCol.Border, Color(0x283142)),
        (ImGuiCol.TitleBg, Color(0x0e1422)),
        (ImGuiCol.TitleBgActive, Color(0x0e1422)),
        (ImGuiCol.TitleBgCollapsed, Color(0x0a0e18)),
        (ImGuiCol.FrameBg, Color(0x151e30)),
        (ImGuiCol.FrameBgHovered, Color(0x213047)),
        (ImGuiCol.FrameBgActive, Color(0x2b3c54)),
        (ImGuiCol.Button, Color(0x253047)),
        (ImGuiCol.ButtonHovered, Color(0x3d4b5c)),
        (ImGuiCol.ButtonActive, Color(0x546173)),
        (ImGuiCol.Header, Color(0xd8b45c, 0.15f)),
        (ImGuiCol.HeaderHovered, Color(0xd8b45c, 0.24f)),
        (ImGuiCol.HeaderActive, Color(0xd8b45c, 0.32f)),
        (ImGuiCol.Tab, Color(0x0e1422)),
        (ImGuiCol.TabHovered, Color(0xd8b45c, 0.24f)),
        (ImGuiCol.TabActive, Color(0xd8b45c, 0.16f)),
        (ImGuiCol.TabUnfocused, Color(0x0a0e18)),
        (ImGuiCol.TabUnfocusedActive, Color(0x1a2231)),
        (ImGuiCol.CheckMark, Color(0xd8b45c)),
        (ImGuiCol.SliderGrab, Color(0xd8b45c)),
        (ImGuiCol.SliderGrabActive, Color(0xf2d99b)),
        (ImGuiCol.PlotHistogram, Color(0xd8b45c)),
        (ImGuiCol.PlotHistogramHovered, Color(0xf2d99b)),
        (ImGuiCol.Separator, Color(0xd8b45c, 0.22f)),
        (ImGuiCol.SeparatorHovered, Color(0xd8b45c, 0.55f)),
        (ImGuiCol.SeparatorActive, Color(0xd8b45c)),
        (ImGuiCol.ResizeGrip, Color(0xd8b45c, 0.15f)),
        (ImGuiCol.ResizeGripHovered, Color(0xd8b45c, 0.45f)),
        (ImGuiCol.ResizeGripActive, Color(0xd8b45c, 0.7f)),
        (ImGuiCol.ScrollbarBg, Color(0x05070d)),
        (ImGuiCol.ScrollbarGrab, Color(0x253047)),
        (ImGuiCol.ScrollbarGrabHovered, Color(0x3d6f8d)),
        (ImGuiCol.ScrollbarGrabActive, Color(0x7fc8ef)),
        (ImGuiCol.TextSelectedBg, Color(0x7fc8ef, 0.25f)),
        (ImGuiCol.NavHighlight, Color(0x7fc8ef)),
        (ImGuiCol.TableHeaderBg, Color(0x151e30)),
        (ImGuiCol.TableBorderStrong, Color(0xd8b45c, 0.22f)),
        (ImGuiCol.TableBorderLight, Color(0xdfe4ec, 0.08f)),
    ];

    public static void Push()
    {
        foreach (var (slot, color) in Colors) ImGui.PushStyleColor(slot, color);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f * ImGuiHelpers.GlobalScale);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(Colors.Length);
    }

    private static Vector4 Color(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);
}
