using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private readonly Func<bool> canPreviewImage;
    private readonly Action<uint> previewImage;
    private readonly IReadOnlySet<uint> previewImageIds;
    private uint selectedScreenImage;

    private void DrawDevTab()
    {
        ImGui.TextWrapped(Loc.Localize("Dev.PreviewHint",
            "Click a radio button to show the banner. Click it again to repeat."));
        ImGui.TextWrapped(Loc.Localize("Dev.ResolutionHint",
            "The game selects SD (.tex) or HD (_hr1.tex). Hover over a title to see the dimensions."));
        var available = this.canPreviewImage();
        if (!available)
        {
            ImGui.TextWrapped(Loc.Localize("Dev.LoginRequired", "Log in to preview banners."));
        }

        using var disabled = ImRaii.Disabled(!available);
        if (ImGui.CollapsingHeader(Loc.Localize("Dev.Cinematics", "Cinematic text"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawImageTable("cinematics", DevScreenImages.Cinematics,
                Loc.Localize("Dev.Cinzel", "Cinzel regular"));
        }

        if (ImGui.CollapsingHeader(Loc.Localize("Dev.Banners", "Banners")))
        {
            this.DrawImageTable("banners", DevScreenImages.Images, Loc.Localize("Dev.Revised", "Revised"));
        }
    }

    private void DrawImageTable(string tableId, (uint Id, uint SourceId, string Name)[] images, string previewLabel)
    {
        using var table = ImRaii.Table(tableId, 4, ImGuiTableFlags.RowBg);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn(Loc.Localize("Dev.Current", "Current"), ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn(previewLabel, ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn(Loc.Localize("Dev.ImageId", "Image ID"), ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn(Loc.Localize("Dev.OriginalLabel", "Original label"));
        ImGui.TableHeadersRow();
        foreach (var (id, _, name) in images)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            this.DrawImageRadio(id);
            ImGui.TableNextColumn();
            var previewId = DevScreenImages.PreviewId(id);
            using (ImRaii.Disabled(!this.previewImageIds.Contains(previewId)))
            {
                this.DrawImageRadio(previewId);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(id.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{name} ({id})");
            if (ImGui.IsItemHovered())
            {
                var height = DevScreenImages.Height(id);
                ImGui.SetTooltip($"{id}.tex: 1280 x {height}\n{id}_hr1.tex: 2560 x {height * 2}");
            }
        }
    }

    private void DrawImageRadio(uint id)
    {
        if (ImGui.RadioButton($"##banner_{id}", this.selectedScreenImage == id))
        {
            this.selectedScreenImage = id;
            this.previewImage(id);
        }
    }
}
