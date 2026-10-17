using System.Globalization;
using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace GubalLibrary;

internal sealed partial class ConfigWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    private static readonly Vector4 Blue = new(0.45f, 0.72f, 1f, 1f);




    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<PageStatus> pageStatus;
    private readonly PackOperations operations;

    private readonly Func<PackContents> contents;

    private readonly ITextureProvider textures;

    private readonly Action onPackPrepared;

    private readonly Action checkForUpdate;

    private readonly Action<bool> setAutoUpdate;

    private readonly Func<bool?> dalamudWaits;

    private readonly Action openDalamudSettings;


    private string? checkedPath;

    private (PackManifest? Manifest, string? Error) checkedPack;

    private string? runningGame;

    private string? installMessage;
    private bool installFailed;

    private string? restartReason;

    private readonly Func<ShadowState?> shadowState;

    public ConfigWindow(
        Configuration config,
        Action<Configuration> save,
        FileDialogManager fileDialogs,
        Func<PageStatus> pageStatus,
        Func<PackContents> contents,
        ITextureProvider textures,
        IDataManager data,
        IGameGui gameGui,
        PackOperations operations,
        Action onPackPrepared,
        Action checkForUpdate,
        Action<bool> setAutoUpdate,
        Func<bool?> dalamudWaits,
        Action openDalamudSettings,
        Func<ShadowState?> shadowState,
        string version,
        bool isDev = false,
        Func<bool>? canPreview = null)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.isDev = isDev;
        this.previewData = isDev ? data : null;
        this.previewGui = isDev ? gameGui : null;
        this.canPreview = canPreview;
        this.shadowState = shadowState;
        this.config = config;
        this.save = save;
        this.fileDialogs = fileDialogs;
        this.pageStatus = pageStatus;
        this.contents = contents;
        this.textures = textures;
        this.operations = operations;
        if (string.IsNullOrWhiteSpace(config.OwnPackFolder) && config.LanguagePackPath.Length > 0
            && !SameSource(config.LanguagePackPath, operations.InstalledPath))
            config.OwnPackFolder = config.LanguagePackPath;
        this.onPackPrepared = onPackPrepared;
        this.checkForUpdate = checkForUpdate;
        this.setAutoUpdate = setAutoUpdate;
        this.dalamudWaits = dalamudWaits;
        this.openDalamudSettings = openDalamudSettings;

        this.Size = new Vector2(1000, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            // Keep the pack version and translation columns readable.
            MinimumSize = new Vector2(760, 220),
            MaximumSize = new Vector2(1100, 800),
        };
    }

    public override void Draw()
    {
        var pages = this.pageStatus();
        var changed = false;

        if (this.operations.HasPending && this.config.ServeLanguagePack)
            this.restartReason = Loc.Localize("Restart.Installed",
                "The new language pack is ready. Restart the game to activate it before the game reads its files.");
        this.DrawRestartBanner();
        ImGui.Spacing();

        using (var bar = ImRaii.TabBar("##gubalTabs"))
        {
            if (bar)
            {
                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Pack", "Language pack")))
                {
                    if (tab)
                    {
                        this.DrawSetupTab(pages, ref changed);
                    }
                }

                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Parts", "Localization settings")))
                {
                    if (tab)
                    {
                        this.DrawPartsTab(ref changed);
                    }
                }

                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Codex", "Unending Codex")))
                {
                    if (tab)
                    {
                        var enabled = this.config.EnableCodexLinks;
                        if (ImGui.Checkbox(Loc.Localize("Codex.Enable", "Enable Unending Codex links on dialog"), ref enabled))
                        {
                            this.config.EnableCodexLinks = enabled;
                            changed = true;
                        }

                        var picture = this.Picture("codex");
                        if (picture != null)
                        {
                            // Crop the preview to the dialogue and its highlighted term.
                            var uv0 = new Vector2(0.295f, 0.777f);
                            var uv1 = new Vector2(0.705f, 0.945f);
                            var crop = new Vector2(picture.Width, picture.Height) * (uv1 - uv0);
                            var width = Math.Min(ImGui.GetContentRegionAvail().X, crop.X);
                            ImGui.Spacing();
                            ImGui.Image(picture.Handle, new Vector2(width, width * crop.Y / crop.X), uv0, uv1);
                        }
                    }
                }

                if (this.isDev) this.DrawDevTab();

                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Help", "Help and contact")))
                {
                    if (tab)
                    {
                        DrawHelpTab();
                    }
                }
            }
        }

        if (changed)
        {
            this.save(this.config);
        }
    }

    private void DrawSetupTab(PageStatus pages, ref bool changed)
    {
        this.DrawInstalledPack(pages);

        ImGui.Separator();
        this.DrawLanguagePackRow(pages, ref changed);
    }

    private void DrawPartsTab(ref bool changed)
    {
        var pack = this.contents();

        if (pack.Layout.Count == 0)
        {
            ImGui.TextWrapped(Loc.Localize("Parts.Empty", "No language pack installed."));
            return;
        }

        this.DrawPartsHeader(pack, ref changed);

        this.DrawPartSelection(pack, ref changed);
        ImGui.Separator();
        this.DrawCompatibility(ref changed);
    }

    private void DrawCompatibility(ref bool changed)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.Localize("Compatibility.Title", "Compatibility"));
        if (ImGui.GetContentRegionAvail().X > 420f * ImGuiHelpers.GlobalScale) ImGui.SameLine();

        var lifestream = this.config.LifestreamCompatibility;
        if (DrawPartSwitch("Lifestream", "lifestream", ref lifestream))
        {
            this.config.LifestreamCompatibility = lifestream;
            this.NoteParted();
            changed = true;
        }

        if (ImGui.GetContentRegionAvail().X > 150f * ImGuiHelpers.GlobalScale) ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
            ImGui.TextColored(Green, FontAwesomeIcon.Check.ToIconString());
        ImGui.SameLine();
        ImGui.TextUnformatted("AutoRetainer");

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(Loc.Localize("Compatibility.AutoRetainer",
                "Supported. Confirmed with AutoRetainer 4.6.2.11."));
        }
    }

    private void DrawPartsHeader(PackContents pack, ref bool changed)
    {
        ImGui.TextWrapped(Loc.Localize("Parts.Intro",
            "Choose which parts of the game to localize."));
        ImGui.Spacing();

        ImGui.TextDisabled(Loc.Localize("Parts.NextStart",
            "Changes here take effect when the game next starts."));
        ImGui.Spacing();

        var total = pack.PartCount;
        var off = pack.PartsOff(this.config.DisabledSheets).Count;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.Format(Loc.Localize("Parts.Count", "{0} of {1} on"), total - off, total));

        var reset = Loc.Localize("Parts.TurnAllOn", "Turn everything on");
        var disable = Loc.Localize("Parts.TurnAllOff", "Turn everything off");
        var width = ImGui.CalcTextSize(reset).X + ImGui.CalcTextSize(disable).X
            + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X >= width)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width);
        }
        else
        {
            ImGui.NewLine();
        }

        using (ImRaii.Disabled(off == 0))
        {
            if (ImGui.Button(reset))
            {
                this.config.DisabledSheets.Clear();
                this.NoteParted();
                changed = true;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(off == total))
        {
            if (ImGui.Button(disable))
            {
                this.SetSheets(pack.Layout.SelectMany(g => g.Parts).SelectMany(p => p.Sheets), false);
                changed = true;
            }
        }

        ImGui.Separator();
    }

    // Shared texture wraps belong to Dalamud and must not be disposed here.
    private IDalamudTextureWrap? Picture(string name)
    {
        if (!HasPicture(name)) return null;
        var resource = $"GubalLibrary.images.tooltips.{name}.png";
        return this.textures.GetFromManifestResource(typeof(Plugin).Assembly, resource)
            .TryGetWrap(out var wrap, out _)
            ? wrap
            : null;
    }

    private IDalamudTextureWrap? Flag(string code)
    {
        var resource = $"GubalLibrary.images.flags.{code.ToLowerInvariant()}.png";
        return this.textures.GetFromManifestResource(typeof(Plugin).Assembly, resource)
            .TryGetWrap(out var wrap, out _)
            ? wrap
            : null;
    }

    private static float FlagHeight => 12f * ImGuiHelpers.GlobalScale;

    private void SetSheets(IEnumerable<string> sheets, bool on)
    {
        foreach (var sheet in sheets)
        {
            if (on)
            {
                this.config.DisabledSheets.Remove(sheet);
            }
            else
            {
                this.config.DisabledSheets.Add(sheet);
            }
        }

        this.NoteParted();
    }

    private void NoteParted()
    {
        this.restartReason ??=
            Loc.Localize("Restart.Parts",
                "You changed which parts are translated. The game reads all of its text once, at "
                + "startup, so this takes effect the next time it starts.");
    }

    private void DrawInstalledPack(PageStatus pages)
    {
        this.DrawCompatibilityNotice(pages);

        if (this.restartReason is null)
        {
            this.DrawUpdateNotice(pages);
        }

        if (!pages.Active || pages.Manifest is null)
        {
            if (this.operations.HasPending && this.config.ServeLanguagePack) return;
            var (icon, colour, text) = pages.Error is { Length: > 0 } error
                ? (FontAwesomeIcon.ExclamationTriangle, Red, error)
                : this.config.LanguagePackPath.Length == 0
                    ? (FontAwesomeIcon.InfoCircle, Amber, Loc.Localize("Pack.None",
                        "No language pack installed. Choose a language below and press Install."))
                    : this.config.ServeLanguagePack
                        ? (FontAwesomeIcon.Hourglass, Amber, Loc.Localize("Pack.Pending",
                            "A language pack is installed but not in use yet. Restart the game."))
                        : (FontAwesomeIcon.InfoCircle, Grey, Loc.Localize("Pack.Off",
                            "The game is showing its own text. The installed language pack is switched off."));

            Icon(icon, colour);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
            return;
        }

        var pack = pages.Manifest;
        var version = pack.TranslationVersion ?? Loc.Localize("Pack.Unversioned", "unversioned");

        var clean = pages.Update.State == UpdateState.UpToDate && pages.Compatibility is null;

        using (ImRaii.PushColor(ImGuiCol.Text, Green, clean))
        {
            ImGui.TextUnformatted(clean
                ? string.Format(Loc.Localize("Pack.UpToDate", "{0}, up to date: {1}"), pack.DisplayName, version)
                : $"{pack.DisplayName} ({version})");
        }

        DrawPackDetail(pack, pages);

        if (this.shadowState() is { Ok: false } failed)
        {
            ImGui.Spacing();
            Icon(FontAwesomeIcon.ExclamationTriangle, Red);
            ImGui.TextWrapped(Loc.Localize("Pack.NothingServed", "NOTHING IS TRANSLATED THIS SESSION"));
            ImGui.PopStyleColor();
            ImGui.TextWrapped(failed.Message);
        }

    }

    private void DrawUpdateNotice(PageStatus pages)
    {
        if (pages.Manifest is null)
        {
            return;
        }

        switch (pages.Update.State)
        {
            case UpdateState.Checking:
                Icon(FontAwesomeIcon.Hourglass, Grey);
                ImGui.TextWrapped(Loc.Localize("Update.Checking",
                    "Checking whether a newer language pack is published..."));
                ImGui.PopStyleColor();
                break;

            case UpdateState.Available when pages.Update.Published is { } update:
                Icon(FontAwesomeIcon.ArrowUp, Amber);
                ImGui.TextWrapped(
                    string.Format(
                        Loc.Localize("Update.Available", "A newer language pack is published: {0}"),
                        update.TranslationVersion)
                    + (update.GameVersion is { Length: > 0 } game
                        ? string.Format(Loc.Localize("Update.BuiltForGame", ", built for game {0}"), game)
                        : string.Empty));
                ImGui.PopStyleColor();

                // No button here: taking it is the chooser's action button, which names the version.
                break;

            case UpdateState.UpToDate:
                break;

            case UpdateState.Unreachable:
                Icon(FontAwesomeIcon.ExclamationTriangle, Red);
                ImGui.TextWrapped(
                    Loc.Localize("Update.Unreachable",
                        "No connection to the language pack update URL. If it persists, this pack "
                        + "will not update itself, so check where you got it from."));
                ImGui.PopStyleColor();
                break;

            case UpdateState.NotDeclared:
                Icon(FontAwesomeIcon.ExclamationTriangle, Amber);
                ImGui.TextWrapped(
                    Loc.Localize("Update.NotDeclared",
                        "This language pack has no update URL, so it will never update itself."));
                ImGui.PopStyleColor();
                break;
        }
    }

    private void DrawRestartBanner()
    {
        if (this.operations.HasPending && this.config.ServeLanguagePack && this.dalamudWaits() is not true)
        {
            ImGui.Separator();
            ImGui.TextColored(Amber, Loc.Localize("Setup.WaitRequired", "Setup required"));
            this.DrawBootWaitState();
            ImGui.Separator();
            return;
        }
        if (this.restartReason is not { Length: > 0 } reason)
        {
            return;
        }

        ImGui.Separator();
        ImGui.SetWindowFontScale(1.25f);

        Icon(FontAwesomeIcon.PowerOff, Amber);
        ImGui.TextWrapped(Loc.Localize("Restart.Title", "RESTART THE GAME"));
        ImGui.PopStyleColor();

        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped(reason);
        ImGui.Separator();
    }

    private static void Icon(FontAwesomeIcon icon, Vector4 colour)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        ImGui.SameLine();
    }

    private void DrawLanguagePackRow(PageStatus pages, ref bool changed)
    {
        this.DrawLanguageTable(pages, ref changed);
        this.DrawAutoUpdateRow();

        if (!this.operations.Installing && this.installMessage is { Length: > 0 } message)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, this.installFailed ? Red : Green);
            ImGui.TextWrapped(message);
            ImGui.PopStyleColor();
        }
    }

    private void DrawLanguageTable(PageStatus pages, ref bool changed)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var buttonPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
            new Vector2(ImGui.GetStyle().FramePadding.X, 1f * scale));
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding,
            new Vector2(ImGui.GetStyle().CellPadding.X, 1f * scale));

        using (var table = ImRaii.Table("##languages", 6,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX))
        {
            if (!table)
            {
                return;
            }

            ImGui.TableSetupColumn(Loc.Localize("Setup.ColLanguage", "Language"), ImGuiTableColumnFlags.WidthFixed);
            var localizedLabel = Loc.Localize("Setup.ColLocalized", "Localized");
            var gameVersionLabel = Loc.Localize("Setup.ColGameVersion", "Game version");
            ImGui.TableSetupColumn(localizedLabel, ImGuiTableColumnFlags.WidthFixed,
                Math.Max(90f * scale, ImGui.CalcTextSize(localizedLabel).X));
            ImGui.TableSetupColumn(gameVersionLabel, ImGuiTableColumnFlags.WidthFixed,
                Math.Max(ImGui.CalcTextSize("2026.09.01").X, ImGui.CalcTextSize(gameVersionLabel).X)
                + ImGui.GetStyle().FramePadding.X * 2);
            ImGui.TableSetupColumn(Loc.Localize("Setup.ColEnglish", "WIP & future localization"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.Localize("Setup.ColLinks", "Links"), ImGuiTableColumnFlags.WidthFixed);
            var actionWidth = new[]
            {
                Loc.Localize("Setup.Ready", "Ready · Restart required"),
                Loc.Localize("Setup.WaitRequired", "Setup required"),
                Loc.Localize("Setup.UseEnglish", "Use English"),
                Loc.Localize("Setup.UseFolder", "Use this folder"),
                Loc.Localize("Setup.Install", "Install"),
            }.Max(label => ImGui.CalcTextSize(label).X) + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.TableSetupColumn(Loc.Localize("Setup.ColAction", "Action"), ImGuiTableColumnFlags.WidthFixed, actionWidth);
            ImGui.TableHeadersRow();

            // Keep catalog indices stable when rows are sorted.
            var rows = KnownPacks.All.Select((pack, index) =>
            {
                var (manifest, loading) = pack.Published ? this.PublishedManifest(pack) : (null, false);
                return (Pack: pack, Index: index, Manifest: manifest, Loading: loading);
            }).OrderByDescending(row => row.Manifest?.Coverage?.Percent ?? -1)
                .ThenBy(row => row.Index);

            foreach (var row in rows)
            {
                var i = row.Index;
                var pack = row.Pack;
                var manifest = row.Manifest;
                var loading = row.Loading;
                var offline = pack.Published && !loading && manifest is null;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                this.DrawLanguageName(pack.Code, null, pack.Name);

                this.DrawCoverageCell(pack, manifest, loading);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                var gameVersion = manifest?.GameVersion ?? string.Empty;
                ImGui.TextUnformatted(gameVersion.EndsWith(".0000.0000", StringComparison.Ordinal)
                    ? gameVersion[..^10] : gameVersion);

                ImGui.TableNextColumn();
                if (manifest is not null)
                {
                    ImGui.AlignTextToFramePadding();
                    DrawTruncatedText(manifest.Coverage?.FutureLocalization is { } future
                        ? string.Join(", ", future.Where(p => p.Pending > 0).Select(p =>
                            p.PendingPercent == 100 ? p.Content ?? p.Sheet : $"{p.Content ?? p.Sheet} ({(p.PendingPercent == 0 ? "<0.1" : p.PendingPercent.ToString("0.#", CultureInfo.InvariantCulture))}% pending)"))
                        : Loc.Localize("Setup.EnglishNotSpecified", "Not specified"));
                }

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (pack.Site is { Length: > 0 } site)
                {
                    IconLink(FontAwesomeIcon.Globe, pack.SiteName ?? site, site);
                    ImGui.SameLine();
                }

                IconLink(FontAwesomeIcon.Language, Loc.Localize("Setup.HelpTranslate", "Help us translate"), KnownPacks.TutorialFor(pack.Code));

                if (PackTracker(pages, i) is { Length: > 0 } tracker)
                {
                    ImGui.SameLine();
                    IconLink(FontAwesomeIcon.Bug, Loc.Localize("Setup.Report", "Report a localization issue"), tracker);
                }

                ImGui.TableNextColumn();
                using (ImRaii.PushId(pack.Code)) this.DrawOfficialPackAction(pack, manifest, pages, loading || offline);
            }

            if (this.config.LanguagePackPath.Length > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                this.DrawLanguageName(null, FontAwesomeIcon.Globe, Loc.Localize("Setup.LanguageEnglish", "English (no localization)"));
                ImGui.TableSetColumnIndex(5);
                ImGui.AlignTextToFramePadding();
                if (!pages.Active)
                {
                    ImGui.TextColored(Green, Loc.Localize("Setup.Active", "Active"));
                    if (this.config.ServeLanguagePack) ImGui.SameLine();
                }
                if (!this.config.ServeLanguagePack && pages.Active)
                    ImGui.TextColored(Amber, Loc.Localize("Setup.Ready", "Ready · Restart required"));
                else if (this.config.ServeLanguagePack)
                {
                    using var disabled = ImRaii.Disabled(this.operations.Installing);
                    if (ImGui.Button(Loc.Localize("Setup.UseEnglish", "Use English")))
                    {
                        this.config.ServeLanguagePack = false;
                        this.installMessage = null;
                        this.restartReason = null;
                        this.NoteServing();
                        changed = true;
                    }
                }
            }

            this.DrawCustomPackRow(pages, ref changed);
        }

    }

    private static void DrawTruncatedText(string text)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var display = text;
        if (ImGui.CalcTextSize(text).X > width)
        {
            var low = 0;
            var high = text.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (ImGui.CalcTextSize(text[..middle] + "...").X <= width)
                    low = middle;
                else
                    high = middle - 1;
            }

            if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
            display = text[..low].TrimEnd() + "...";
        }

        ImGui.TextUnformatted(display);
        if (ImGui.IsItemHovered()) SetTooltip(text);
    }

    private static void IconLink(FontAwesomeIcon icon, string label, string url)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Blue))
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        SetTooltip(label + "\n" + url);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Util.OpenLink(url);
        }
    }

    private void DrawLanguageName(string? flagCode, FontAwesomeIcon? glyph, string label)
    {
        var rowY = ImGui.GetCursorPosY();
        if (flagCode is not null && this.Flag(flagCode) is { } flag)
        {
            ImGui.SetCursorPosY(rowY + (ImGui.GetFrameHeight() - FlagHeight) / 2f);
            ImGui.Image(flag.Handle, new Vector2(FlagHeight * flag.Width / flag.Height, FlagHeight));
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
        }
        else if (glyph is { } icon)
        {
            ImGui.AlignTextToFramePadding();
            using (ImRaii.PushFont(UiBuilder.IconFont, true))
            {
                ImGui.TextUnformatted(icon.ToIconString());
            }

            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
    }

    private void DrawCoverageCell(KnownPack pack, PackManifest? manifest, bool loading)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();

        if (!pack.Published)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.NoPackYet", "No pack yet"));
        }
        else if (loading)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.CoverageLoading", "Loading..."));
        }
        else if (manifest is null)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.Offline", "Offline"));
        }
        else if (manifest.Coverage is { } coverage)
        {
            var percent = coverage.Percent;
            var overlay = percent >= 100 ? "100 %"
                : percent is > 0 and < 1 ? "<1 %"
                : percent.ToString("0.#", CultureInfo.InvariantCulture) + " %";

            ImGui.ProgressBar((float)Math.Clamp(percent / 100.0, 0.0, 1.0), new Vector2(-1, 0), overlay);
            if (pack.Site is { Length: > 0 } site && ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    Util.OpenLink(site);
                }
            }

            this.DrawCoverageTooltip(coverage);
        }
    }


    // The manifest published at a pack's release, fetched once per session.

    private (PackManifest? Manifest, bool Loading) PublishedManifest(KnownPack pack) =>
        this.operations.Published(pack);

    private static bool SameSource(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool IsInstalledOfficialPack(KnownPack pack, PageStatus pages) =>
        SameSource(this.config.LanguagePackPath, this.operations.InstalledPath)
        && (SameSource(pack.Source, this.config.InstalledFrom)
            || string.Equals(pack.Code, pages.Manifest?.Language, StringComparison.OrdinalIgnoreCase));

    private void DrawOfficialPackAction(KnownPack pack, PackManifest? published, PageStatus pages, bool unavailable)
    {
        var installed = this.IsInstalledOfficialPack(pack, pages);
        if (installed && pages.Update is { State: UpdateState.Available, Published: { } update }) published = update;
        var moved = installed && !SameSource(pack.Source, this.config.InstalledFrom);
        var newer = installed && pages.Manifest is { } current && published is not null
            && (string.CompareOrdinal(published.TranslationVersion, current.TranslationVersion) > 0
                || published.GameVersion == this.RunningGame() && current.GameVersion != published.GameVersion);
        if (this.DrawPackActionState(pack.Source, installed && pages.Active && !newer && !moved)) return;
        if (!pack.Published)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.NoPackYet", "No pack yet"));
            return;
        }
        using var disabled = ImRaii.Disabled(this.operations.Installing || unavailable || pack.Source is null);
        var isUpdate = newer || moved;
        var label = isUpdate ? Loc.Localize("Setup.Update", "Update") : Loc.Localize("Setup.Install", "Install");
        using var buttonColor = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.16f, 0.45f, 0.20f, 1f), isUpdate);
        using var hoverColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.56f, 0.25f, 1f), isUpdate);
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.12f, 0.35f, 0.16f, 1f), isUpdate);
        if (ImGui.Button(label))
        {
            this.Install(pack.Source!);
        }
        if (ImGui.IsItemHovered()) SetTooltip(Loc.Localize("Setup.InstallTip", "Prepare this language pack for the next game start."));
    }

    private bool DrawPackActionState(string? source, bool active)
    {
        ImGui.AlignTextToFramePadding();
        if (SameSource(source, this.operations.InstallingSource))
        {
            this.DrawInstallProgress();
            return true;
        }
        if (this.config.ServeLanguagePack && SameSource(source, this.operations.PendingSource))
        {
            ImGui.TextColored(Amber, this.dalamudWaits() is true
                ? Loc.Localize("Setup.Ready", "Ready · Restart required")
                : Loc.Localize("Setup.WaitRequired", "Setup required"));
            return true;
        }
        if (this.config.ServeLanguagePack && active)
        {
            ImGui.TextColored(Green, Loc.Localize("Setup.Active", "Active"));
            return true;
        }
        return false;
    }

    private void DrawInstallProgress()
    {
        var progress = this.operations.Progress;
        ImGui.ProgressBar(progress.Fraction ?? -1f * (float)ImGui.GetTime(),
            new Vector2(-1, ImGui.GetFrameHeight()), progress.Label);
        if (ImGui.IsItemHovered()) SetTooltip(progress.Detail);
    }

    private void DrawCustomPackRow(PageStatus pages, ref bool changed)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        this.DrawLanguageName(null, FontAwesomeIcon.FolderOpen, Loc.Localize("Setup.LanguageOwn", "Custom"));

        ImGui.TableSetColumnIndex(3);
        var folder = this.config.OwnPackFolder;
        using (ImRaii.Disabled(this.operations.Installing))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##customFolder", ref folder, 2048))
            {
                this.config.OwnPackFolder = folder;
                changed = true;
            }
        }
        var (manifest, error) = this.FolderPack();
        if (ImGui.IsItemHovered()) SetTooltip(error ?? folder);

        ImGui.TableSetColumnIndex(4);
        using (ImRaii.Disabled(this.operations.Installing))
            if (ImGui.Button(Loc.Localize("Setup.Browse", "Browse..."))) this.BrowseForLanguagePack();

        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
        var version = manifest?.GameVersion ?? string.Empty;
        ImGui.TextUnformatted(version.EndsWith(".0000.0000", StringComparison.Ordinal) ? version[..^10] : version);

        ImGui.TableSetColumnIndex(5);
        var active = pages.Active && !SameSource(this.config.LanguagePackPath, this.operations.InstalledPath)
            && (SameSource(folder, this.config.LanguagePackPath) || SameSource(folder, this.config.InstalledFrom));
        if (this.DrawPackActionState(folder, active)) return;
        using var disabled = ImRaii.Disabled(this.operations.Installing || manifest is null || string.IsNullOrWhiteSpace(folder));
        if (ImGui.Button(Loc.Localize("Setup.UseFolder", "Use this folder")))
        {
            this.Install(folder.Trim());
        }
        if (ImGui.IsItemHovered()) SetTooltip(error ?? Loc.Localize("Setup.InstallTip", "Prepare this language pack for the next game start."));
    }

    private (PackManifest? Manifest, string? Error) FolderPack()
    {
        var folder = this.config.OwnPackFolder.Trim();
        if (string.Equals(this.checkedPath, folder, StringComparison.OrdinalIgnoreCase))
        {
            return this.checkedPack;
        }

        this.checkedPath = folder;
        this.checkedPack = Directory.Exists(folder)
            ? PackManifest.Read(folder)
            : (null, string.Format(Loc.Localize("Setup.OwnNoFolder", "No folder at '{0}'."), folder));

        return this.checkedPack;
    }

    private string? RunningGame() => this.runningGame ??= ExdRedirector.RunningGameVersion() ?? string.Empty;

    private void NoteServing()
    {
        this.restartReason ??=
            Loc.Localize("Restart.Serving",
                "You changed the language. The game reads all of its text once, at startup, so this "
                + "takes effect the next time it starts.");
    }

    private void DrawAutoUpdateRow()
    {
        var available = this.config.LanguagePackPath.Length > 0
                        && PackInstaller.IsRemote(this.config.PackSource)
                        && string.Equals(
                            this.config.LanguagePackPath,
                            this.operations.InstalledPath,
                            StringComparison.OrdinalIgnoreCase);

        if (!available)
        {
            return;
        }

        var auto = this.config.AutoUpdatePack;
        if (ImGui.Checkbox(Loc.Localize("Setup.Auto", "Auto-update at start"), ref auto))
        {
            this.setAutoUpdate(auto);
        }

        if (auto)
        {
            ImGui.Indent();
            this.DrawBootWaitState();
            ImGui.Unindent();
        }
    }

    private void DrawBootWaitState()
    {
        if (this.dalamudWaits() is true)
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGui.TextWrapped(Loc.Localize("Boot.NotWaiting",
            "Enable \"Wait for plugins before game loads\" in Dalamud's General settings to allow language pack activation and startup updates."));
        ImGui.PopStyleColor();

        if (ImGui.Button($"{Loc.Localize("Boot.OpenSettings", "Open Dalamud settings")}##bootWait"))
        {
            this.openDalamudSettings();
        }

        SetTooltip(Loc.Localize("Boot.WhereTip",
            "It is on the General tab, called \"Wait for plugins before game loads\"."));
    }

    private void Install(string source)
    {
        this.installMessage = null;
        this.operations.Install(source, this.config, this.save, this.onPackPrepared, result =>
        {
            this.installFailed = !result.Success;
            if (!result.Success)
            {
                this.installMessage = result.Error;
                return;
            }
            var pack = result.Manifest!;
            this.installMessage = string.Format(
                Loc.Localize("Install.Done", "Prepared {0} ({1}) for the next game start."),
                pack.DisplayName,
                pack.TranslationVersion ?? Loc.Localize("Pack.Unversioned", "unversioned"));
            this.restartReason = Loc.Localize("Restart.Installed",
                "The new language pack is ready. Restart the game to activate it before the game reads its files.");
        });
    }

    private static void DrawPackDetail(PackManifest pack, PageStatus pages)
    {
        var language = pack.LanguageName ?? pack.Language ?? Loc.Localize("Pack.UnknownLanguage", "unknown language");
        var identity = pack.Author is { Length: > 0 } author
            ? string.Format(Loc.Localize("Pack.LanguageByAuthor", "{0} by {1}"), language, author)
            : language;

        var detail = identity + " | " + string.Format(
            Loc.Localize("Pack.BuiltFor", "Built for game {0}"),
            pack.GameVersion ?? Loc.Localize("Pack.UnknownVersion", "unknown"));

        if (pages.Active)
        {
            detail += " | " + string.Format(
                Loc.Localize("Pack.Served", "{0} read(s) answered from disk this session"),
                pages.ServedCount.ToString("N0"));
        }

        ImGui.TextDisabled(detail);

        if (pages.Active)
        {
            if (pages.FontCount > 0)
            {
                ImGui.TextDisabled(string.Format(
                    Loc.Localize("Pack.Fonts", "{0} of {1} font file(s) served from the pack"),
                    pages.FontsServedCount.ToString("N0"),
                    pages.FontCount.ToString("N0")));
            }
        }

    }

    private static void DrawHelpTab()
    {
        Icon(FontAwesomeIcon.Bug, Blue);
        Link(Loc.Localize("Help.ReportBug", "Report a bug"), KnownPacks.PluginIssues);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Icon(FontAwesomeIcon.Comments, Blue);
        ImGui.TextDisabled(Loc.Localize("Contact.Discord", "Discord"));
        ImGui.PopStyleColor();
        ImGui.SameLine();

        // Selectable rather than text: a handle has to be copyable to be worth anything.
        if (ImGui.Selectable(KnownPacks.Discord, false, ImGuiSelectableFlags.None,
                ImGui.CalcTextSize(KnownPacks.Discord)))
        {
            ImGui.SetClipboardText(KnownPacks.Discord);
        }

        SetTooltip(Loc.Localize("Contact.Copy", "Click to copy."));
    }

    private static string? PackTracker(PageStatus pages, int chosen)
    {
        var manifest = pages.Manifest;
        var known = chosen >= 0 ? KnownPacks.All[chosen] : KnownPacks.ForCode(manifest?.Language);

        if (known is not { } pack)
        {
            return manifest?.IssuesUrl;
        }

        return string.Equals(pack.Code, manifest?.Language, StringComparison.OrdinalIgnoreCase)
            ? manifest?.IssuesUrl ?? pack.Issues
            : pack.Issues;
    }

    private static void Link(string label, string url)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Blue))
        {
            ImGui.TextUnformatted(label);
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList()
            .AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), ImGui.GetColorU32(Blue));

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        SetTooltip(url);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Util.OpenLink(url);
        }
    }

    private void BrowseForLanguagePack()
    {
        var startPath = this.config.LanguagePackPath;
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            startPath = string.Empty;
        }

        this.fileDialogs.OpenFolderDialog(
            Loc.Localize("Setup.PickerTitle", "Select the folder your language pack was built into"),
            (confirmed, selected) =>
            {
                if (!confirmed || string.IsNullOrWhiteSpace(selected))
                {
                    return;
                }

                this.config.OwnPackFolder = selected;
                this.save(this.config);
            },
            startPath,
            isModal: false);
    }

    private static void SetTooltip(string text)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(400f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}

internal readonly record struct PageStatus(
    bool Active,
    int PageCount,
    int ServedCount,
    int FontCount,
    int FontsServedCount,
    string? Error,
    PackManifest? Manifest,
    UpdateStatus Update,
    CompatibilitySelection? Compatibility = null);
