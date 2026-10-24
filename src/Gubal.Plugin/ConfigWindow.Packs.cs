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
    private const float PackFooterSize = 50f;
    private const float PackIconSize = PackFooterSize;

    private float PackCardMinimumWidth()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var actionWidth = new[]
        {
            Loc.Localize("Setup.Install", "Install"),
            Loc.Localize("Setup.UseEnglish", "Use English"),
            Loc.Localize("Setup.Active", "Active"),
        }.Max(label => this.PackActionButtonSize(label).X);
        return Math.Max(280f * scale,
            (PackIconSize * 4 + 24f) * scale
            + ImGui.GetStyle().ItemSpacing.X * 4 + actionWidth);
    }

    private static float PackCardHeight() => Math.Max(200f * ImGuiHelpers.GlobalScale,
        ImGui.GetFrameHeight() * 2 + ImGui.GetTextLineHeightWithSpacing() * 4 + 42f * ImGuiHelpers.GlobalScale);

    private void UpdateInitialPackSize()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 12f * scale;
        var viewport = ImGui.GetMainViewport().WorkSize;
        var padding = ImGui.GetStyle().WindowPadding.X * 2;
        var cardWidth = this.PackCardMinimumWidth();
        var width = Math.Min(3 * (cardWidth + spacing) + padding,
            Math.Max(760f * scale, viewport.X - 32f * scale));
        var columns = Math.Clamp((int)((width - padding + spacing) / (cardWidth + spacing)), 1, 3);
        var rows = (KnownPacks.All.Length + 2 + columns - 1) / columns;
        var height = rows * (PackCardHeight() + spacing) + 180f * scale;
        this.Size = new Vector2(width,
            Math.Min(height, Math.Max(420f * scale, viewport.Y - 32f * scale))) / scale;
    }

    private void DrawLanguageCards(PageStatus pages, ref bool changed)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 12f * scale;
        var minimumWidth = this.PackCardMinimumWidth();
        var columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + spacing) / (minimumWidth + spacing)), 1, 3);
        var height = PackCardHeight();
        var rows = KnownPacks.All.Select((pack, index) =>
        {
            var (manifest, loading) = pack.Published ? this.PublishedManifest(pack) : (null, false);
            return (Pack: pack, Index: index, Manifest: manifest, Loading: loading);
        }).OrderByDescending(row => row.Manifest?.Coverage?.Percent ?? -1).ThenBy(row => row.Index).ToArray();

        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(spacing / 2));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f * scale));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f * scale);
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg,
            ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg]);
        using var table = ImRaii.Table("##languageCards", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings);
        if (!table) return;

        for (var index = 0; index < rows.Length + 2; index++)
        {
            ImGui.TableNextColumn();
            var official = index < rows.Length;
            var english = index == rows.Length;
            var id = official ? rows[index].Pack.Code : english ? "english" : "custom";
            var active = official ? pages.Active && this.IsInstalledOfficialPack(rows[index].Pack, pages)
                : english ? !pages.Active : this.IsCustomPackActive(pages);
            using var border = ImRaii.PushColor(ImGuiCol.Border, Green, active);
            using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize,
                ImGui.GetStyle().ChildBorderSize + (active ? scale : 0));
            using var child = ImRaii.Child("##pack-" + id, new Vector2(ImGui.GetContentRegionAvail().X, height), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            if (!child) continue;
            using var scope = ImRaii.PushId(id);
            if (official)
            {
                var row = rows[index];
                this.DrawOfficialPackCard(row.Pack, row.Index, row.Manifest, row.Loading, pages);
            }
            else if (english) this.DrawEnglishPackCard(pages, ref changed);
            else this.DrawCustomPackCard(pages, ref changed);
        }
    }

    private void DrawOfficialPackCard(KnownPack pack, int index, PackManifest? manifest, bool loading, PageStatus pages)
    {
        this.DrawLanguageName(pack.Code, null, pack.Name);
        ImGui.Spacing();
        this.DrawPackCoverage(pack, manifest, loading);
        ImGui.Spacing();
        DrawPackVersion(manifest);
        BeginPackCardFooter();
        if (this.DrawPackReviewer(manifest)) ImGui.SameLine();
        this.DrawPackLinks(pack, index, pages);
        ImGui.SameLine();
        this.DrawOfficialPackAction(pack, manifest, pages, loading || pack.Published && manifest is null);
    }

    private void DrawPackCoverage(KnownPack? pack, PackManifest? manifest, bool loading)
    {
        ImGui.TextDisabled(Loc.Localize("Setup.LocalizationProgress", "Localization progress"));
        if (manifest?.Coverage is { } coverage)
        {
            {
                var label = CoveragePercent(coverage.Percent);
                var iconSize = Math.Max(ImGui.GetTextLineHeight() * 1.1f, 24f * ImGuiHelpers.GlobalScale);
                var hasWip = coverage.FutureLocalization?.Any(item => item.Pending > 0) is true;
                var iconWidth = hasWip ? iconSize + ImGui.GetStyle().ItemSpacing.X : 0;
                ImGui.SameLine();
                AlignPackAction(label, leadingWidth: iconWidth);
                if (hasWip)
                {
                    this.DrawPackWip(manifest, iconSize);
                    ImGui.SameLine();
                }
                ImGui.TextColored(ThemeGold, label);
            }
            ImGui.ProgressBar((float)Math.Clamp(coverage.Percent / 100.0, 0, 1),
                new Vector2(-1, 7f * ImGuiHelpers.GlobalScale), string.Empty);
            if (ImGui.IsItemHovered() && pack?.Site is { Length: > 0 } site)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Util.OpenLink(site);
            }
            this.DrawCoverageTooltip(coverage);
        }
        else
        {
            ImGui.TextDisabled(pack is { Published: false } ? Loc.Localize("Setup.NoPackYet", "No pack yet")
                : loading ? Loc.Localize("Setup.CoverageLoading", "Loading...")
                : manifest is null ? Loc.Localize("Setup.Offline", "Offline")
                : Loc.Localize("Setup.EnglishNotSpecified", "Not specified"));
        }
    }

    private bool DrawPackReviewer(PackManifest? manifest)
    {
        var size = PackIconSize * ImGuiHelpers.GlobalScale;
        var reviewers = manifest?.ReviewerNames.ToArray() ?? [];
        if (reviewers.Length > 0)
        {
            if (this.textures.GetFromManifestResource(typeof(Plugin).Assembly, "GubalLibrary.images.reviewer.png")
                    .TryGetWrap(out var image, out _))
            {
                DrawPackIcon(image, size);
                if (ImGui.IsItemHovered())
                {
                    using var tooltip = ImRaii.Tooltip();
                    ImGui.TextUnformatted(Loc.Localize("Setup.Reviewers", "Reviewers"));
                    foreach (var reviewer in reviewers) ImGui.BulletText("@" + reviewer);
                }
                return true;
            }
        }
        return false;
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
        var unknown = Loc.Localize("Pack.UnknownVersion", "unknown");
        var translationVersion = string.IsNullOrEmpty(manifest.TranslationVersion) ? unknown : manifest.TranslationVersion;
        var gameVersion = string.IsNullOrEmpty(manifest.GameVersion) ? unknown : manifest.GameVersion;
        if (gameVersion.EndsWith(".0000.0000", StringComparison.Ordinal)) gameVersion = gameVersion[..^10];
        using var color = ImRaii.PushColor(ImGuiCol.Text, Grey);
        ImGui.TextWrapped(string.Format(CultureInfo.CurrentCulture,
            Loc.Localize("Setup.PackVersion", "Version {0} ({1})"), translationVersion, gameVersion));
    }

    private void DrawPackLinks(KnownPack pack, int index, PageStatus pages)
    {
        ImGui.AlignTextToFramePadding();
        if (pack.Site is { Length: > 0 } site)
        {
            this.PackImageLink("website", FontAwesomeIcon.Globe, pack.SiteName ?? site, site);
            ImGui.SameLine();
        }
        this.PackImageLink("localization", FontAwesomeIcon.Language,
            Loc.Localize("Setup.HelpTranslate", "Help us translate"), KnownPacks.TutorialFor(pack.Code));
        if (PackTracker(pages, index) is { Length: > 0 } tracker)
        {
            ImGui.SameLine();
            this.PackImageLink("bug-report", FontAwesomeIcon.Bug,
                Loc.Localize("Setup.Report", "Report a localization issue"), tracker);
        }
    }

    private void PackImageLink(string name, FontAwesomeIcon fallback, string label, string url)
    {
        var resource = $"GubalLibrary.images.links.{name}.png";
        if (!this.textures.GetFromManifestResource(typeof(Plugin).Assembly, resource).TryGetWrap(out var image, out _))
        {
            IconLink(fallback, label, url);
            return;
        }
        DrawPackIcon(image, PackIconSize * ImGuiHelpers.GlobalScale);
        if (!ImGui.IsItemHovered()) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        SetTooltip(label + "\n" + url);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Util.OpenLink(url);
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

    private void BeginPackCardFooter() => ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(),
        ImGui.GetWindowSize().Y - ImGui.GetStyle().WindowPadding.Y
        - Math.Max(ImGui.GetFrameHeight(), PackFooterSize * ImGuiHelpers.GlobalScale)));

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
        this.DrawLanguageName(null, FontAwesomeIcon.Globe, Loc.Localize("Setup.LanguageEnglishTitle", "English"));
        ImGui.Spacing();
        ImGui.TextWrapped(Loc.Localize("Setup.EnglishDescription", "Use the game's original English text."));
        BeginPackCardFooter();
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
        var useEnglish = Loc.Localize("Setup.UseEnglish", "Use English");
        AlignPackAction(useEnglish, button: true);
        using var disabled = ImRaii.Disabled(this.operations.Installing);
        if (!DrawPackActionButton(useEnglish)) return;
        this.config.ServeLanguagePack = false;
        this.installMessage = null;
        this.restartReason = null;
        this.NoteServing();
        changed = true;
    }

    private bool IsCustomPackActive(PageStatus pages) => pages.Active
        && !SameSource(this.config.LanguagePackPath, this.operations.InstalledPath)
        && (SameSource(this.config.OwnPackFolder, this.config.LanguagePackPath)
            || SameSource(this.config.OwnPackFolder, this.config.InstalledFrom));

    private void DrawCustomPackCard(PageStatus pages, ref bool changed)
    {
        var (manifest, error) = this.FolderPack();
        this.DrawLanguageName(null, FontAwesomeIcon.FolderOpen, Loc.Localize("Setup.LanguageOwn", "Custom"));
        ImGui.Spacing();
        if (manifest?.Coverage is not null)
        {
            this.DrawPackCoverage(null, manifest, false);
            ImGui.Spacing();
        }
        DrawPackVersion(manifest);
        ImGui.Spacing();
        var folder = this.config.OwnPackFolder;
        var browse = Loc.Localize("Setup.Browse", "Browse...");
        var browseWidth = ImGui.CalcTextSize(browse).X + ImGui.GetStyle().FramePadding.X * 2;
        using (ImRaii.Disabled(this.operations.Installing))
        {
            ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - browseWidth - ImGui.GetStyle().ItemSpacing.X));
            if (ImGui.InputText("##customFolder", ref folder, 2048))
            {
                this.config.OwnPackFolder = folder;
                (manifest, error) = this.FolderPack();
                changed = true;
            }
            SetTooltip(error ?? folder);
            ImGui.SameLine();
            if (ImGui.Button(browse)) this.BrowseForLanguagePack();
        }
        BeginPackCardFooter();
        if (this.DrawPackReviewer(manifest)) ImGui.SameLine();
        if (this.DrawPackActionState(folder, this.IsCustomPackActive(pages))) return;
        var useFolder = Loc.Localize("Setup.UseFolder", "Use this folder");
        AlignPackAction(useFolder, button: true);
        using var disabled = ImRaii.Disabled(this.operations.Installing || manifest is null || string.IsNullOrWhiteSpace(folder));
        if (DrawPackActionButton(useFolder)) this.Install(folder.Trim());
        SetTooltip(error ?? Loc.Localize("Setup.InstallTip", "Prepare this language pack for the next game start."));
    }
}
