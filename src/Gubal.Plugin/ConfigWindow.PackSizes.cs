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

    private Vector2 DrawPackSizes(string action, PackManifest? manifest, string? detail = null, bool isNotice = false,
        float extraWidth = 0)
    {
        var labels = PackSizeLabels(manifest);
        var storage = this.operations.Storage(manifest);
        var gap = 10f * ImGuiHelpers.GlobalScale;
        var size = this.ui.MeasureActionButton(action, detail: detail, extraWidth: extraWidth);
        var width = labels.LabelWidth + gap + labels.ValueWidth;
        var start = ImGui.GetCursorScreenPos();
        start.X += Math.Max(0, ImGui.GetContentRegionAvail().X - width - gap - size.X);
        var download = PackStorage.FormatBytes(manifest?.DownloadBytes);
        var installed = PackStorage.FormatBytes(manifest?.InstalledBytes);
        var downloadInk = PluginUi.TextInkBounds(download);
        var installedInk = PluginUi.TextInkBounds(installed);
        var labelInk = PluginUi.TextInkBounds(labels.Download + labels.Installed);
        var top = Math.Min(labelInk.Top, Math.Min(downloadInk.Top, installedInk.Top));
        var bottom = Math.Max(labelInk.Bottom, Math.Max(downloadInk.Bottom, installedInk.Bottom));
        var lineStep = ImGui.GetTextLineHeightWithSpacing();
        var height = bottom - top + lineStep;
        var position = start + new Vector2(0, Math.Max(0, (size.Y - height) / 2));
        using (ImRaii.Group())
        {
            DrawSizeText(labels.Download, position, Grey, top, bottom);
            DrawSizeText(download, position + new Vector2(width - ImGui.CalcTextSize(download).X, 0),
                ImGui.GetStyle().Colors[(int)ImGuiCol.Text], top, bottom);
            position.Y += lineStep;
            var color = storage is { Insufficient: true } ? DiskSpaceWarningColor : Grey;
            DrawSizeText(labels.Installed, position, color, top, bottom);
            DrawStorageTooltip(storage);
            DrawSizeText(installed, position + new Vector2(width - ImGui.CalcTextSize(installed).X, 0),
                storage is { Insufficient: true } ? DiskSpaceWarningColor : ImGui.GetStyle().Colors[(int)ImGuiCol.Text], top, bottom);
            DrawStorageTooltip(storage);
        }
        ImGui.SameLine();
        var noticeSpace = isNotice ? Math.Max(0, gap - ImGui.GetStyle().ItemInnerSpacing.X) : 0;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width + gap - noticeSpace, start.Y));
        return new Vector2(size.X + noticeSpace, size.Y);
    }

    private static void DrawSizeText(string text, Vector2 position, Vector4 color, float top, float bottom)
    {
        ImGui.SetCursorScreenPos(position);
        ImGui.GetWindowDrawList().AddText(position - new Vector2(0, top), ImGui.GetColorU32(color), text);
        ImGui.Dummy(new Vector2(ImGui.CalcTextSize(text).X, bottom - top));
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
