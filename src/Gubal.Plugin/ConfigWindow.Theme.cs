using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private static readonly Vector4 ThemeInk = ThemeColor(0x05070d);
    private static readonly Vector4 ThemeGold = PluginTheme.Gold;
    public override void PreDraw()
    {
        base.PreDraw();
        this.UpdateInitialPackSize();
        PluginTheme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        if (this.requestedWindowSize is { } size)
        {
            this.Size = size / ImGuiHelpers.GlobalScale;
            this.SizeCondition = ImGuiCond.Always;
            this.requestedWindowSize = null;
        }
        else this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        PluginTheme.Pop();
        base.PostDraw();
    }

    private static Vector4 ThemeColor(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

    private bool DrawPackActionButton(string label) => this.ui.DrawActionButton(label, true);

    private Vector2 PackActionButtonSize(string label, string? detail = null) => this.ui.MeasureActionButton(label, detail: detail);

    private void DrawPackActiveBadge()
    {
        using var font = this.ui.ActionFont.Push();
        var label = Loc.Localize("Setup.Active", "Active");
        var size = this.PackActionButtonSize(label);
        this.AlignPackAction(label, button: true);
        var position = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(Green),
            5f * ImGuiHelpers.GlobalScale);
        draw.AddText(position + (size - ImGui.CalcTextSize(label)) / 2,
            ImGui.GetColorU32(ThemeInk), label);
        PluginUi.EmphasizeText(position + (size - ImGui.CalcTextSize(label)) / 2,
            ImGui.GetColorU32(ThemeInk), label);
        ImGui.Dummy(size);
    }

}
