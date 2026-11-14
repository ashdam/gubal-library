using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private void DrawBugReportInformation()
    {
        var pages = this.pageStatus();
        var none = Loc.Localize("Library.None", "None");
        var active = pages.Active && pages.Manifest is { } manifest
            ? KnownPacks.All.FirstOrDefault(pack => SameSource(pack.Source, pages.Source)).Name
                ?? Loc.Localize("Setup.LanguageOwn", "Custom") + " (" + manifest.DisplayName + ")"
            : none;
        var next = !this.config.ServeLanguagePack && pages.Active ? Loc.Localize("Setup.LanguageEnglishTitle", "English")
            : this.operations.HasPending && this.config.ServeLanguagePack
                ? KnownPacks.All.FirstOrDefault(pack => SameSource(pack.Source, this.operations.PendingSource)).Name
                    ?? Loc.Localize("Setup.LanguageOwn", "Custom")
                : Loc.Localize("Library.Unchanged", "Unchanged");
        var information = string.Join('\n',
            "Gubal Library: " + this.pluginVersion,
            Loc.Localize("Report.GameVersion", "Game version") + ": " + this.RunningGame(),
            Loc.Localize("Library.ActivePack", "Active pack") + ": " + active,
            Loc.Localize("Library.PackVersion", "Pack version") + ": " + (pages.Active ? pages.Manifest?.TranslationVersion ?? none : none),
            Loc.Localize("Library.NextStart", "Next start") + ": " + next);
        var iconWidth = ImGui.GetFrameHeight();
        var textWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - iconWidth - ImGui.GetStyle().WindowPadding.X * 2
            - ImGui.GetStyle().ItemSpacing.X);
        var height = ImGui.CalcTextSize(information, false, textWidth).Y + ImGui.GetStyle().WindowPadding.Y * 2;
        using (ImRaii.PushColor(ImGuiCol.ChildBg, ThemeInk))
        using (var block = ImRaii.Child("##bugReportInformation", new Vector2(0, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (block)
            {
                using var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
                using var columns = ImRaii.Table("##bugReportInformationColumns", 2,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings);
                if (columns)
                {
                    ImGui.TableSetupColumn("##information", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("##copy", ImGuiTableColumnFlags.WidthFixed, iconWidth);
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(information);
                    ImGui.TableNextColumn();
                    if (PluginUi.SmallIconButton("copyBugReport", FontAwesomeIcon.Copy,
                            Loc.Localize("Library.CopyInformation", "Copy to clipboard")))
                        ImGui.SetClipboardText(information);
                    SetTooltip(Loc.Localize("Library.CopyInformation", "Copy to clipboard"));
                }
            }
        }
        ImGui.TextWrapped(Loc.Localize("Library.StatusCommand", "Use /gubal status in the game for current diagnostic information."));
    }
}
