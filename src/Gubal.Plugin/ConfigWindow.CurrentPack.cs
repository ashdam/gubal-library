using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private string? activeManifestSource;
    private DateTime activeManifestStamp;
    private DateTime nextActiveManifestCheck;
    private PackManifest? activeSourceManifest;

    private PackManifest? ActiveLocalManifest(string source)
    {
        if (DateTime.UtcNow < this.nextActiveManifestCheck && SameSource(source, this.activeManifestSource)) return this.activeSourceManifest;
        this.nextActiveManifestCheck = DateTime.UtcNow.AddSeconds(2);
        try
        {
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(source, PackManifest.FileName));
            if (!SameSource(source, this.activeManifestSource) || stamp != this.activeManifestStamp)
            {
                this.activeSourceManifest = PackManifest.Read(source).Manifest;
                this.activeManifestSource = source;
                this.activeManifestStamp = stamp;
            }
        }
        catch (IOException) { this.activeSourceManifest = null; }
        catch (UnauthorizedAccessException) { this.activeSourceManifest = null; }
        return this.activeSourceManifest;
    }

    private void DrawCurrentPack(PageStatus pages)
    {
        if (!pages.Active || pages.Manifest is not { } manifest)
        {
            this.DrawInstalledPack(pages);
            return;
        }
        this.DrawCompatibilityNotice(pages);
        if (this.restartReason is null) this.DrawUpdateNotice(pages);
        var known = KnownPacks.ForCode(manifest.Language);
        if (known is { } candidate && !this.IsInstalledOfficialPack(candidate, pages)) known = null;
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
                        ImGui.TextDisabled(Loc.Localize("Library.ActivePack", "Active pack"));
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
        if (this.shadowState() is { Ok: false } failed)
        {
            ImGui.TextColored(Red, Loc.Localize("Pack.NothingServed", "NOTHING IS TRANSLATED THIS SESSION"));
            ImGui.TextWrapped(failed.Message);
        }
        ImGui.Spacing();
    }

    private void DrawCurrentPackAction(KnownPack? known, PackManifest manifest, PageStatus pages)
    {
        var source = known?.Source ?? pages.Source ?? this.config.InstalledFrom;
        var local = !string.IsNullOrWhiteSpace(source) && !PackInstaller.IsRemote(source) && Directory.Exists(source);
        var published = local ? this.ActiveLocalManifest(source)
            : known is { } pack ? this.PublishedManifest(pack).Manifest : null;
        var update = local
            ? published is not null && (string.CompareOrdinal(published.TranslationVersion, manifest.TranslationVersion) > 0
                || published.GameVersion != manifest.GameVersion) ? published : null
            : pages.Update.State == UpdateState.Available ? pages.Update.Published : null;
        var canUpdate = update is not null && !string.IsNullOrWhiteSpace(source);
        var chooseCurrent = !this.config.ServeLanguagePack
            || this.operations.HasPending && !SameSource(source, this.operations.PendingSource);
        var label = chooseCurrent ? Loc.Localize("Library.UsePack", "Use this pack")
            : canUpdate ? Loc.Localize("Setup.Update", "Update")
            : pages.Update.State == UpdateState.Unreachable ? Loc.Localize("Library.TryAgain", "Try again")
            : Loc.Localize("Library.CheckUpdates", "Check for updates");
        if (SameSource(source, this.operations.InstallingSource)
            || this.config.ServeLanguagePack && SameSource(source, this.operations.PendingSource))
        {
            this.DrawPackActionState(source, false, update ?? published ?? manifest);
            return;
        }
        if (local && !canUpdate && !chooseCurrent)
        {
            this.DrawPackSizes(Loc.Localize("Setup.Active", "Active"), published ?? manifest);
            this.DrawPackActiveBadge();
            return;
        }
        var nextVersion = canUpdate && !chooseCurrent ? update?.TranslationVersion : null;
        this.DrawPackSizes(label, update ?? published ?? manifest, nextVersion);
        using var disabled = ImRaii.Disabled(this.operations.Installing || !local && pages.Update.State == UpdateState.Checking);
        if (this.ui.DrawActionButton(label, canUpdate || chooseCurrent, detail: nextVersion))
        {
            if (canUpdate || chooseCurrent)
                this.RequestPackChange(source, known?.Name ?? Loc.Localize("Setup.LanguageOwn", "Custom"), update ?? published ?? manifest);
            else this.checkForUpdate();
        }
    }
}
