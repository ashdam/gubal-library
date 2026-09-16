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
    private string bannerFilter = string.Empty;

    private void DrawDevTab()
    {
        ImGui.TextUnformatted(Loc.Localize("Dev.CorrectedBanners", "Banners with corrected letters"));
        ImGui.TextWrapped(Loc.Localize("Dev.PreviewHint",
            "Click a radio button to show the banner. Click it again to repeat."));
        ImGui.TextWrapped(Loc.Localize("Dev.Resolutions",
            "Each variant has SD and HD. The game selects the resolution."));
        var available = this.canPreviewImage();
        if (!available)
        {
            ImGui.TextWrapped(Loc.Localize("Dev.LoginRequired", "Log in to preview banners."));
        }

        ImGui.InputText(Loc.Localize("Dev.Filter", "Filter by text or texture ID"), ref this.bannerFilter, 128);
        using var disabled = ImRaii.Disabled(!available);
        foreach (var banner in DevScreenImages.Banners)
        {
            var title = Loc.Localize($"Dev.Banner.{banner.Texture}", banner.Name);
            if (!string.IsNullOrWhiteSpace(this.bannerFilter)
                && !$"{banner.Texture} {title} {banner.Name}".Contains(this.bannerFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ImGui.TextUnformatted($"{banner.Texture} · {title}");
            var height = banner.Texture is 120108 or 120109 or 120119 ? 128 : 360;
            ImGui.TextDisabled($"SD: {banner.Texture}.tex (1280 × {height}) · HD: {banner.Texture}_hr1.tex (2560 × {height * 2})");
            this.DrawImageRadio(banner.Original, Loc.Localize("Dev.Original", "Original game texture"));
            ImGui.SameLine();
            this.DrawImageRadio(banner.Translated, Loc.Localize("Dev.Translated", "Translated texture"));
            ImGui.Spacing();
        }
    }

    private void DrawImageRadio(uint id, string label)
    {
        using var disabled = ImRaii.Disabled(!this.previewImageIds.Contains(id));
        if (ImGui.RadioButton($"{label}##banner_{id}", this.selectedScreenImage == id))
        {
            this.selectedScreenImage = id;
            this.previewImage(id);
        }
    }
}
