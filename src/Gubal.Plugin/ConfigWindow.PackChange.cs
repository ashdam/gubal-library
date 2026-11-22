using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private sealed record PackChange(string? Source, string Name, PackManifest? Manifest);
    private PackChange? requestedPackChange;
    private bool openPackChange;

    private void RequestPackChange(string? source, string name, PackManifest? manifest = null)
    {
        this.requestedPackChange = new(source, name, manifest);
        this.openPackChange = true;
    }

    private void DrawPackChangeDialog(ref bool changed)
    {
        if (this.openPackChange)
        {
            ImGui.OpenPopup("###libraryPackChange");
            this.openPackChange = false;
        }
        if (this.requestedPackChange is not { } request) return;
        ImGui.SetNextWindowSize(new Vector2(460f * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Appearing);
        var open = true;
        if (!ImGui.BeginPopupModal(Loc.Localize("Library.ChangePack", "Change language pack") + "###libraryPackChange",
                ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped(string.Format(Loc.Localize("Library.PreparePack", "Prepare {0} for the next game start?"), request.Name));
        ImGui.Spacing();
        if (request.Manifest is { } manifest)
        {
            ImGui.TextWrapped($"{Loc.Localize("Setup.DownloadSize", "Download")}: {PackStorage.FormatBytes(manifest.DownloadBytes)}");
            ImGui.TextWrapped($"{Loc.Localize("Setup.InstalledSize", "On disk")}: {PackStorage.FormatBytes(manifest.InstalledBytes)}");
        }
        ImGui.TextWrapped(Loc.Localize("Library.CurrentSession", "The text in your current session will not change. Close the game fully and start it again to use the new settings."));
        ImGui.Spacing();
        var cancel = Loc.Localize("Report.Cancel", "Cancel");
        var confirm = Loc.Localize("Library.ConfirmPack", "Prepare for next start");
        var buttonRowWidth = this.ui.MeasureActionButton(cancel).X + ImGui.GetStyle().ItemSpacing.X
            + this.ui.MeasureActionButton(confirm).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - buttonRowWidth));
        if (this.ui.DrawActionButton(cancel, false)) ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (this.ui.DrawActionButton(confirm, true))
        {
            if (request.Source is { } source) this.Install(source);
            else
            {
                this.config.ServeLanguagePack = false;
                this.installMessage = null;
                this.restartReason = null;
                this.NoteServing();
                changed = true;
            }
            this.requestedPackChange = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
