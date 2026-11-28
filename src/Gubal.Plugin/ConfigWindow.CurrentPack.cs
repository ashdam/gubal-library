using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private void DrawCurrentPack(PageStatus pages)
    {
        if (!pages.Active || pages.Manifest is not { } manifest)
        {
            this.DrawInstalledPack(pages);
            return;
        }
        this.DrawCompatibilityNotice(pages);
        var known = KnownPacks.ForCode(manifest.Language);
        if (known is { } candidate && !this.IsInstalledOfficialPack(candidate, pages)) known = null;
        if (known is not null) this.DrawUpdateNotice(pages);
        var scale = ImGuiHelpers.GlobalScale;
        var wide = ImGui.GetContentRegionAvail().X >= 620f * scale;
        var line = ImGui.GetTextLineHeightWithSpacing();
        float nameHeight;
        using (this.ui.HeadingFont.Push()) nameHeight = ImGui.GetFrameHeight();
        nameHeight = Math.Max(nameHeight, PackNameReviewerSize * scale);
        var leftHeight = line * 2 + nameHeight + 31f * scale + ImGui.GetStyle().ItemSpacing.Y * 4;
        var actionHeight = PackFooterSize * scale;
        var height = leftHeight + (wide ? 0 : actionHeight + ImGui.GetStyle().ItemSpacing.Y)
            + ImGui.GetStyle().WindowPadding.Y * 2 + ImGui.GetStyle().CellPadding.Y * 2;
        using (ImRaii.PushColor(ImGuiCol.Border, ThemeColor(0xd8b45c, 0.85f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f * scale))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 3f * scale))
        using (var card = ImRaii.Child("##currentPack", new Vector2(0, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                using (var columns = ImRaii.Table("##currentPackColumns", wide ? 2 : 1, ImGuiTableFlags.SizingStretchSame))
                {
                    if (columns)
                    {
                        ImGui.TableNextColumn();
                        var top = ImGui.GetCursorPosY();
                        ImGui.TextColored(Green, Loc.Localize("Library.ActivePack", "Active pack"));
                        this.DrawPackNameWithReviewers(known?.Code, known is null ? FontAwesomeIcon.FolderOpen : null,
                            known?.Name ?? Loc.Localize("Setup.LanguageOwn", "Custom"), manifest, true, known?.Site);
                        ImGui.Spacing();
                        this.DrawPackCoverage(known, manifest, false);
                        DrawPackVersion(manifest);
                        ImGui.TableNextColumn();
                        if (wide) ImGui.SetCursorPosY(top + Math.Max(0, leftHeight - actionHeight));
                        this.DrawCurrentPackAction(known, manifest, pages);
                    }
                }
            }
        }
        if (this.shadowState() is { Ok: false, CacheLocked: false } failed)
        {
            ImGui.TextColored(Red, Loc.Localize("Pack.NothingServed", "NOTHING IS TRANSLATED THIS SESSION"));
            ImGui.TextWrapped(failed.Message);
        }
        ImGui.Spacing();
    }

    private void DrawCurrentPackAction(KnownPack? known, PackManifest manifest, PageStatus pages)
    {
        var source = known?.Source ?? pages.Source ?? this.config.InstalledFrom;
        if (known is null)
        {
            this.DrawPackSizes(Loc.Localize("Setup.Active", "Active"), manifest);
            this.DrawPackActiveBadge();
            return;
        }
        var published = this.PublishedManifest(known.Value).Manifest;
        var update = pages.Update.State == UpdateState.Available ? pages.Update.Published : null;
        var canUpdate = update is not null && !string.IsNullOrWhiteSpace(source);
        var chooseCurrent = !this.config.ServeLanguagePack
            || this.operations.HasPending && !SameSource(source, this.operations.PendingSource);
        var label = chooseCurrent ? Loc.Localize("Library.UsePack", "Use this pack")
            : canUpdate ? Loc.Localize("Setup.Update", "Update")
            : pages.Update.State == UpdateState.Unreachable ? Loc.Localize("Library.TryAgain", "Try again")
            : Loc.Localize("Library.CheckUpdates", "Check for updates");
        var nextVersion = canUpdate && !chooseCurrent ? update?.TranslationVersion : null;
        if (SameSource(source, this.operations.InstallingSource)
            || this.config.ServeLanguagePack && SameSource(source, this.operations.PendingSource))
        {
            this.DrawPackActionState(source, false, label, update ?? published ?? manifest, nextVersion);
            return;
        }
        this.DrawPackSizes(label, update ?? published ?? manifest, nextVersion);
        var checking = pages.Update.State == UpdateState.Checking;
        using var disabled = ImRaii.Disabled(this.operations.Installing || checking);
        if (this.ui.DrawActionButton(label, canUpdate || chooseCurrent, detail: nextVersion))
        {
            if (canUpdate || chooseCurrent)
                this.RequestPackChange(source, known?.Name ?? Loc.Localize("Setup.LanguageOwn", "Custom"), update ?? published ?? manifest);
            else this.checkForUpdate();
        }
    }
}
