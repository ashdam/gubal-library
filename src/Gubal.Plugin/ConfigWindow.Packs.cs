using System.Globalization;
using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private const float PackFooterSize = PluginTheme.ActionHeight;
    private const float PackIconSize = PackFooterSize;
    private const float PackNameReviewerSize = 36f;

    private float PackCardMinimumWidth(PageStatus pages)
    {
        var minimum = 340f * ImGuiHelpers.GlobalScale;
        foreach (var pack in KnownPacks.All)
        {
            if (pages.Active && this.IsInstalledOfficialPack(pack, pages)) continue;
            var manifest = this.operations.Published(pack).Manifest;
            var pending = this.config.ServeLanguagePack && SameSource(pack.Source, this.operations.PendingSource);
            var label = pending
                ? this.dalamudWaits() is true ? Loc.Localize("Library.Ready", "Ready for next start") : Loc.Localize("Setup.WaitRequired", "Setup required")
                : Loc.Localize("Setup.Install", "Install");
            var reviewerWidth = manifest?.ReviewerNames.Any() is true ? (PackNameReviewerSize + 18f) * ImGuiHelpers.GlobalScale : 0;
            float nameWidth;
            using (this.ui.ActionFont.Push()) nameWidth = this.PackNameWidth(pack);
            var width = Math.Max(nameWidth + reviewerWidth + 24f * ImGuiHelpers.GlobalScale,
                (this.PackActionButtonSize(label).X + PackSizeWidth(manifest) + 12f * ImGuiHelpers.GlobalScale) * 2
                + 24f * ImGuiHelpers.GlobalScale);
            minimum = Math.Max(minimum, width);
        }
        return minimum;
    }

    private float EnglishPackCardHeight()
    {
        return PackFooterSize * ImGuiHelpers.GlobalScale + 24f * ImGuiHelpers.GlobalScale;
    }

    private float CompactPackCardHeight(float cardWidth, IEnumerable<PackManifest?> manifests)
    {
        float nameHeight;
        using (this.ui.ActionFont.Push()) nameHeight = ImGui.GetFrameHeight();
        nameHeight = Math.Max(nameHeight, PackNameReviewerSize * ImGuiHelpers.GlobalScale);
        var width = Math.Max(1, (cardWidth - 24f * ImGuiHelpers.GlobalScale) / 2);
        var versionHeight = manifests.Select(manifest => ImGui.CalcTextSize(PackVersionText(manifest), false, width).Y)
            .DefaultIfEmpty(ImGui.GetTextLineHeight()).Max();
        var summaryHeight = Math.Max(ImGui.GetTextLineHeight(), 24f * ImGuiHelpers.GlobalScale)
            + 7f * ImGuiHelpers.GlobalScale + versionHeight + ImGui.GetStyle().ItemSpacing.Y * 2;
        return nameHeight + Math.Max(PackFooterSize * ImGuiHelpers.GlobalScale, summaryHeight)
            + 24f * ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemSpacing.Y * 3;
    }

    private void UpdateInitialPackSize()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGui.GetMainViewport().WorkSize;
        this.Size = new Vector2(Math.Min(1180f * scale, Math.Max(760f * scale, viewport.X - 32f * scale)),
            Math.Min(820f * scale, Math.Max(420f * scale, viewport.Y - 32f * scale))) / scale;
    }

    private void DrawLanguageCards(PageStatus pages, ref bool changed)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 12f * scale;
        var minimumWidth = this.PackCardMinimumWidth(pages);
        var columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + spacing) / (minimumWidth + spacing)), 1, 2);
        var rows = KnownPacks.All.Select((pack, index) => (pack, index))
            .Where(row => !pages.Active || !this.IsInstalledOfficialPack(row.pack, pages))
            .Select(row =>
        {
            var (pack, index) = row;
            var (manifest, loading) = pack.Published ? this.PublishedManifest(pack) : (null, false);
            return (Pack: pack, Index: index, Manifest: manifest, Loading: loading);
        }).OrderByDescending(row => row.Manifest?.Coverage?.Percent ?? -1).ThenBy(row => row.Index).ToArray();
        var cardWidth = (ImGui.GetContentRegionAvail().X - spacing * columns) / columns;
        var height = this.CompactPackCardHeight(cardWidth, rows.Select(row => row.Manifest));

        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(spacing / 2));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f * scale));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f * scale);
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg,
            ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg]);
        using var table = ImRaii.Table("##languageCards", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings);
        if (!table) return;

        for (var index = 0; index < rows.Length + 1; index++)
        {
            ImGui.TableNextColumn();
            var official = index < rows.Length;
            var id = official ? rows[index].Pack.Code : "english";
            var active = official ? pages.Active && this.IsInstalledOfficialPack(rows[index].Pack, pages)
                : !pages.Active;
            using var border = ImRaii.PushColor(ImGuiCol.Border, Green, active);
            using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize,
                active ? 3f * scale : ImGui.GetStyle().ChildBorderSize);
            using var child = ImRaii.Child("##pack-" + id, new Vector2(ImGui.GetContentRegionAvail().X,
                official ? height : this.EnglishPackCardHeight()), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            if (!child) continue;
            using var scope = ImRaii.PushId(id);
            if (official)
            {
                var row = rows[index];
                this.DrawOfficialPackCard(row.Pack, row.Index, row.Manifest, row.Loading, pages);
            }
            else this.DrawEnglishPackCard(pages, ref changed);

        }
    }

    private void DrawOfficialPackCard(KnownPack pack, int index, PackManifest? manifest, bool loading, PageStatus pages)
    {
        this.DrawPackNameWithReviewers(pack.Code, null, pack.Name, manifest, site: pack.Site);
        ImGui.Spacing();
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero))
        using (var columns = ImRaii.Table("##packSummary", 2,
                   ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings))
        {
            if (columns)
            {
                ImGui.TableNextColumn();
                var top = ImGui.GetCursorPosY();
                this.DrawPackCoverage(pack, manifest, loading);
                DrawPackVersion(manifest);
                var summaryBottom = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y;
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(top + Math.Max(0, summaryBottom - top - PackFooterSize * ImGuiHelpers.GlobalScale));
                this.DrawOfficialPackAction(pack, manifest, pages, loading || pack.Published && manifest is null);
            }
        }
    }

    private float PackNameWidth(KnownPack pack)
    {
        var flagWidth = this.Flag(pack.Code) is { } flag ? FlagHeight * flag.Width / flag.Height + ImGui.GetStyle().ItemSpacing.X : 0;
        return ImGui.CalcTextSize(pack.Name).X + flagWidth + 2f * ImGuiHelpers.GlobalScale;
    }

    private void DrawPackCoverage(KnownPack? pack, PackManifest? manifest, bool loading)
    {
        var rowTop = ImGui.GetCursorPosY();
        var rowHeight = Math.Max(ImGui.GetTextLineHeight(), 24f * ImGuiHelpers.GlobalScale);
        using (this.ui.ActionFont.Push()) rowHeight = Math.Max(rowHeight, ImGui.GetTextLineHeight());
        ImGui.SetCursorPosY(rowTop + (rowHeight - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextDisabled(Loc.Localize("Setup.LocalizationProgress", "Localization progress"));
        if (manifest?.Coverage is { } coverage)
        {
            using var font = this.ui.ActionFont.Push();
            var label = CoveragePercent(coverage.Percent);
            var iconSize = rowHeight;
            var hasWip = coverage.FutureLocalization?.Any(item => item.Pending > 0) is true;
            var iconWidth = hasWip ? iconSize + ImGui.GetStyle().ItemSpacing.X : 0;
            ImGui.SameLine();
            AlignPackAction(label, leadingWidth: iconWidth);
            if (hasWip)
            {
                ImGui.SetCursorPosY(rowTop + (rowHeight - iconSize) / 2);
                this.DrawPackWip(manifest, iconSize);
                ImGui.SameLine();
            }
            ImGui.SetCursorPosY(rowTop + (rowHeight - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(ThemeGold, label);
        }
        else
        {
            var label = pack is { Published: false } ? Loc.Localize("Setup.NoPackYet", "No pack yet")
                : loading ? Loc.Localize("Setup.CoverageLoading", "Loading...")
                : manifest is null ? Loc.Localize("Setup.Offline", "Offline")
                : Loc.Localize("Setup.NoProgressData", "No progress data");
            ImGui.SameLine();
            this.AlignPackAction(label);
            ImGui.TextDisabled(label);
        }
        ImGui.SetCursorPosY(rowTop + rowHeight + ImGui.GetStyle().ItemSpacing.Y);
        var barSize = new Vector2(-1, 7f * ImGuiHelpers.GlobalScale);
        if (manifest?.Coverage is not { } progress)
        {
            ImGui.Dummy(new Vector2(0, barSize.Y));
            return;
        }
        PluginUi.ProgressBar((float)Math.Clamp(progress.Percent / 100.0, 0, 1), barSize, string.Empty);
        if (ImGui.IsItemHovered() && pack?.Site is { Length: > 0 } site)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Util.OpenLink(site);
        }
        this.DrawCoverageTooltip(progress);
    }

    private void DrawPackNameWithReviewers(string? flagCode, FontAwesomeIcon? glyph, string label,
        PackManifest? manifest, bool prominent = false, string? site = null)
    {
        var top = ImGui.GetCursorPosY();
        float nameHeight;
        using (prominent ? this.ui.HeadingFont.Push() : this.ui.ActionFont.Push()) nameHeight = ImGui.GetFrameHeight();
        var reviewerSize = PackNameReviewerSize * ImGuiHelpers.GlobalScale;
        var hasReviewers = manifest?.ReviewerNames.Any() is true;
        var height = hasReviewers ? Math.Max(nameHeight, reviewerSize) : nameHeight;
        ImGui.SetCursorPosY(top + (height - nameHeight) / 2);
        this.DrawLanguageName(flagCode, glyph, label, prominent, site);
        if (hasReviewers)
        {
            ImGui.SameLine(0, 18f * ImGuiHelpers.GlobalScale);
            ImGui.SetCursorPosY(top + (height - reviewerSize) / 2);
            this.DrawPackReviewer(manifest, reviewerSize);
        }
        ImGui.SetCursorPosY(top + height + ImGui.GetStyle().ItemSpacing.Y);
    }

    private bool DrawPackReviewer(PackManifest? manifest, float? size = null)
    {
        var reviewers = manifest?.ReviewerNames.ToArray() ?? [];
        if (reviewers.Length == 0) return false;
        if (!this.textures.GetFromManifestResource(typeof(Plugin).Assembly, "GubalLibrary.images.reviewer.png")
                .TryGetWrap(out var image, out _)) return false;
        DrawPackIcon(image, size ?? PackIconSize * ImGuiHelpers.GlobalScale);
        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.TextUnformatted(Loc.Localize("Setup.Reviewers", "Reviewers"));
            foreach (var reviewer in reviewers) ImGui.BulletText(reviewer);
        }
        return true;
    }

    private void DrawPackWip(PackManifest? manifest, float size)
    {
        var future = manifest?.Coverage?.FutureLocalization;
        if (future?.Any(item => item.Pending > 0) is true)
        {
            if (this.textures.GetFromManifestResource(typeof(Plugin).Assembly, "GubalLibrary.images.links.pickaxe.png")
                    .TryGetWrap(out var image, out _))
                DrawPackIcon(image, size);
            else
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, Amber);
                using var font = ImRaii.PushFont(UiBuilder.IconFont, true);
                ImGui.TextUnformatted(FontAwesomeIcon.Hammer.ToIconString());
            }
            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                ImGui.TextUnformatted(Loc.Localize("Setup.ColEnglish", "WIP & future localization"));
                ImGui.Separator();
                foreach (var item in future.Where(item => item.Pending > 0))
                {
                    var text = item.Content ?? item.Sheet ?? string.Empty;
                    if (item.PendingPercent < 100)
                        text += " · " + (item.PendingPercent == 0 ? "<0.1" : item.PendingPercent.ToString("0.#", CultureInfo.InvariantCulture))
                            + Loc.Localize("Setup.PendingPercent", "% pending");
                    ImGui.BulletText(text);
                }
            }
        }
    }

    private static void DrawPackVersion(PackManifest? manifest)
    {
        if (manifest is null) return;
        using var color = ImRaii.PushColor(ImGuiCol.Text, Grey);
        ImGui.TextWrapped(PackVersionText(manifest));
    }

    private static string PackVersionText(PackManifest? manifest)
    {
        if (manifest is null) return string.Empty;
        var unknown = Loc.Localize("Pack.UnknownVersion", "unknown");
        var translationVersion = string.IsNullOrEmpty(manifest.TranslationVersion) ? unknown : manifest.TranslationVersion;
        var gameVersion = string.IsNullOrEmpty(manifest.GameVersion) ? unknown : manifest.GameVersion;
        if (gameVersion.EndsWith(".0000.0000", StringComparison.Ordinal)) gameVersion = gameVersion[..^10];
        return string.Format(CultureInfo.CurrentCulture,
            Loc.Localize("Setup.PackVersion", "Version {0} ({1})"), translationVersion, gameVersion);
    }

    private static void DrawPackIcon(IDalamudTextureWrap image, float size)
    {
        var position = ImGui.GetCursorScreenPos();
        var end = position + new Vector2(size);
        var rounding = Math.Min(size * 0.2f, 7f * ImGuiHelpers.GlobalScale);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, end, ImGui.GetColorU32(ThemeColor(0xeee8d9)), rounding);
        draw.AddRect(position, end, ImGui.GetColorU32(ThemeColor(0xd8b45c, 0.4f)), rounding);
        ImGui.Image(image.Handle, new Vector2(size));
    }

    private void AlignPackAction(string label, bool button = false, float leadingWidth = 0)
    {
        var available = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var width = leadingWidth + (button ? PackActionButtonSize(label).X : ImGui.CalcTextSize(label).X);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, available - width));
        if (!button && width > available)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - Math.Max(0,
                ImGui.CalcTextSize(label, false, available).Y - ImGui.GetTextLineHeight()));
    }

    private void DrawEnglishPackCard(PageStatus pages, ref bool changed)
    {
        var useEnglish = Loc.Localize("Setup.UseEnglish", "Use English");
        var state = !this.config.ServeLanguagePack
            ? pages.Active ? Loc.Localize("Setup.Ready", "Ready · Restart required") : Loc.Localize("Setup.Active", "Active")
            : useEnglish;
        var actionWidth = this.PackActionButtonSize(state).X;
        var top = ImGui.GetCursorPosY();
        using (ImRaii.Group())
        {
            this.DrawLanguageName(null, FontAwesomeIcon.Globe, Loc.Localize("Setup.LanguageEnglishTitle", "English"));
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + Math.Max(1,
                       ImGui.GetContentRegionAvail().X - actionWidth - ImGui.GetStyle().ItemSpacing.X)))
                ImGui.TextWrapped(Loc.Localize("Setup.EnglishDescription", "Use the game's original English text."));
        }
        ImGui.SameLine();
        ImGui.SetCursorPosY(top);
        if (!this.config.ServeLanguagePack)
        {
            if (!pages.Active)
            {
                this.DrawPackActiveBadge();
                return;
            }
            var label = pages.Active ? Loc.Localize("Setup.Ready", "Ready · Restart required") : Loc.Localize("Setup.Active", "Active");
            AlignPackAction(label);
            using var color = ImRaii.PushColor(ImGuiCol.Text, pages.Active ? Amber : Green);
            ImGui.TextWrapped(label);
            return;
        }
        AlignPackAction(useEnglish, button: true);
        using var disabled = ImRaii.Disabled(this.operations.Installing);
        if (DrawPackActionButton(useEnglish))
            this.RequestPackChange(null, Loc.Localize("Setup.LanguageEnglishTitle", "English"));
    }

    private bool IsCustomPackActive(PageStatus pages) => pages.Active && SameSource(this.config.OwnPackFolder, pages.Source);

    private void DrawLocalPackSettings(PageStatus pages, ref bool changed)
    {
        var folder = this.config.OwnPackFolder;
        var browse = Loc.Localize("Setup.Browse", "Browse...");
        var browseWidth = PluginUi.MeasureButton(browse).X;
        using (ImRaii.Disabled(this.operations.Installing))
        {
            ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - browseWidth - ImGui.GetStyle().ItemSpacing.X));
            if (PluginUi.TextField("##customFolder", ref folder, 2048))
            {
                this.config.OwnPackFolder = folder;
                changed = true;
            }
            SetTooltip(folder);
            ImGui.SameLine();
            if (PluginUi.Button(browse)) this.BrowseForLanguagePack();
        }
        var (manifest, error) = this.FolderPack();
        var scale = ImGuiHelpers.GlobalScale;
        var height = this.CompactPackCardHeight(ImGui.GetContentRegionAvail().X, [manifest]);
        var active = this.IsCustomPackActive(pages);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f * scale)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f * scale))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 3f * scale, active))
        using (ImRaii.PushColor(ImGuiCol.Border, ThemeColor(0xd8b45c, 0.85f), active))
        using (var card = ImRaii.Child("##localPackSettings", new Vector2(0, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card) this.DrawCustomPackCard(manifest, error, pages);
        }
        if (!string.IsNullOrWhiteSpace(error)) ImGui.TextWrapped(error);
    }

    private void DrawCustomPackCard(PackManifest? manifest, string? error, PageStatus pages)
    {
        this.DrawPackNameWithReviewers(null, FontAwesomeIcon.FolderOpen, Loc.Localize("Setup.LanguageOwn", "Custom"), manifest);
        ImGui.Spacing();
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
        using var columns = ImRaii.Table("##packSummary", 2,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings);
        if (!columns) return;
        ImGui.TableNextColumn();
        var top = ImGui.GetCursorPosY();
        this.DrawPackCoverage(null, manifest, false);
        DrawPackVersion(manifest);
        var summaryBottom = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y;
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(top + Math.Max(0, summaryBottom - top - PackFooterSize * ImGuiHelpers.GlobalScale));
        var folder = this.config.OwnPackFolder;
        if (this.DrawPackActionState(folder, this.IsCustomPackActive(pages), manifest)) return;
        var useFolder = Loc.Localize("Setup.UseFolder", "Use this folder");
        this.DrawPackSizes(useFolder, manifest);
        using var disabled = ImRaii.Disabled(this.operations.Installing || manifest is null || string.IsNullOrWhiteSpace(folder));
        if (DrawPackActionButton(useFolder)) this.RequestPackChange(folder.Trim(), manifest!.DisplayName, manifest);
        SetTooltip(error ?? Loc.Localize("Setup.InstallTip", "Prepare this language pack for the next game start."));
    }
}
