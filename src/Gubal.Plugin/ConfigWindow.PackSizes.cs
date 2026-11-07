using CheapLoc;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private static readonly Vector4 DiskSpaceWarningColor = ThemeColor(0xff4040);

    private static (string Download, string Installed, float LabelWidth, float ValueWidth) PackSizeLabels(PackManifest? manifest = null)
    {
        var download = Loc.Localize("Setup.DownloadSize", "Download");
        var installed = Loc.Localize("Setup.InstalledSize", "On disk");
        return (download, installed,
            Math.Max(ImGui.CalcTextSize(download).X, ImGui.CalcTextSize(installed).X),
            Math.Max(ImGui.CalcTextSize(PackStorage.FormatBytes(manifest?.DownloadBytes)).X,
                ImGui.CalcTextSize(PackStorage.FormatBytes(manifest?.InstalledBytes)).X));
    }

    private static float PackSizeWidth(PackManifest? manifest)
    {
        var labels = PackSizeLabels(manifest);
        return labels.LabelWidth + labels.ValueWidth + 22f * ImGuiHelpers.GlobalScale;
    }

    private void DrawPackSizes(string action, PackManifest? manifest)
    {
        var labels = PackSizeLabels(manifest);
        var storage = this.operations.Storage(manifest);
        var gap = 10f * ImGuiHelpers.GlobalScale;
        var size = this.PackActionButtonSize(action);
        var width = labels.LabelWidth + gap + labels.ValueWidth;
        var start = ImGui.GetCursorPos();
        start.X += Math.Max(0, ImGui.GetContentRegionAvail().X - width - gap - size.X);
        var height = ImGui.GetTextLineHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.SetCursorPos(start + new Vector2(0, Math.Max(0, (size.Y - height) / 2)));
        using (var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(0, ImGui.GetStyle().ItemSpacing.Y / 2)))
        using (var table = ImRaii.Table("##packSizes", 2,
                   ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadOuterX, new Vector2(width, 0)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, labels.LabelWidth + gap);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthFixed, labels.ValueWidth);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled(labels.Download);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(PackStorage.FormatBytes(manifest?.DownloadBytes));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (storage is { Insufficient: true }) ImGui.TextColored(DiskSpaceWarningColor, labels.Installed);
                else ImGui.TextDisabled(labels.Installed);
                DrawStorageTooltip(storage);
                ImGui.TableNextColumn();
                if (storage is { Insufficient: true }) ImGui.TextColored(DiskSpaceWarningColor, PackStorage.FormatBytes(manifest?.InstalledBytes));
                else ImGui.TextUnformatted(PackStorage.FormatBytes(manifest?.InstalledBytes));
                DrawStorageTooltip(storage);
            }
        }
        ImGui.SameLine();
        ImGui.SetCursorPos(new Vector2(start.X + width + gap, start.Y));
    }

    private static void DrawStorageTooltip(PackStorageStatus? storage)
    {
        if (!ImGui.IsItemHovered() || storage is not { } status) return;
        if (status.Error is not null)
        {
            SetTooltip(Loc.Localize("Setup.DiskSpaceUnknown", "Could not check free disk space.") + "\n" + status.Path);
            return;
        }
        SetTooltip((status.Insufficient ? Loc.Localize("Setup.InsufficientDiskSpace", "Not enough free disk space.") + "\n" : string.Empty)
            + status.Path + "\n" + string.Format(CultureInfo.CurrentCulture,
                Loc.Localize("Setup.DiskSpaceDetails", "Required: {0}\nAvailable: {1}"),
                PackStorage.FormatBytes(status.RequiredBytes), PackStorage.FormatBytes(status.AvailableBytes)));
    }
}
