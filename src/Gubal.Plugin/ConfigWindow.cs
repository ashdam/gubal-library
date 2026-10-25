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
    private static readonly Vector4 Green = ThemeColor(0x7fc9a0);
    private static readonly Vector4 Amber = ThemeColor(0xe0a35c);
    private static readonly Vector4 Red = ThemeColor(0xef8888);
    private static readonly Vector4 Grey = ThemeColor(0x98a3b6);

    private static readonly Vector4 Blue = ThemeColor(0x7fc8ef);

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
        Func<bool>? canPreview = null)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.previewData = data;
        this.previewGui = gameGui;
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

        this.Size = new Vector2(1000, 680);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            // Keep language cards usable at the minimum width.
            MinimumSize = new Vector2(760, 420),
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

                if (this.DevToolsEnabled) this.DrawDevTab();

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

    private static float FlagHeight => 18f * ImGuiHelpers.GlobalScale;

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
        this.DrawLanguageCards(pages, ref changed);
        this.DrawAutoUpdateRow();

        if (!this.operations.Installing && this.installMessage is { Length: > 0 } message)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, this.installFailed ? Red : Green);
            ImGui.TextWrapped(message);
            ImGui.PopStyleColor();
        }
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
        ImGui.SetWindowFontScale(1.35f);
        try
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ThemeGold))
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
                EmphasizeText(ImGui.GetItemRectMin(), ImGui.GetColorU32(ThemeGold), label);
            }
        }
        finally
        {
            ImGui.SetWindowFontScale(1f);
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
            var noPack = Loc.Localize("Setup.NoPackYet", "No pack yet");
            AlignPackAction(noPack);
            ImGui.TextDisabled(noPack);
            return;
        }
        using var disabled = ImRaii.Disabled(this.operations.Installing || unavailable || pack.Source is null);
        var isUpdate = newer || moved;
        var label = isUpdate ? Loc.Localize("Setup.Update", "Update") : Loc.Localize("Setup.Install", "Install");
        AlignPackAction(label, button: true);
        if (DrawPackActionButton(label))
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
            var label = this.dalamudWaits() is true
                ? Loc.Localize("Setup.Ready", "Ready · Restart required")
                : Loc.Localize("Setup.WaitRequired", "Setup required");
            AlignPackAction(label);
            using var color = ImRaii.PushColor(ImGuiCol.Text, Amber);
            ImGui.TextWrapped(label);
            return true;
        }
        if (this.config.ServeLanguagePack && active)
        {
            this.DrawPackActiveBadge();
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

    private void DrawHelpTab()
    {
        var report = Loc.Localize("Help.ReportBug", "Report a bug");
        this.PackImageLink("bug-report", FontAwesomeIcon.Bug, report, KnownPacks.PluginIssues);
        ImGui.SameLine();
        Link(report, KnownPacks.PluginIssues);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!this.DrawLinkImage("email"))
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont, true);
            ImGui.TextColored(Blue, FontAwesomeIcon.Envelope.ToIconString());
        }
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextDisabled(Loc.Localize("Contact.Email", "Email"));
        foreach (var email in KnownPacks.ContactEmails)
        {
            if (ImGui.Selectable(email, false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(email)))
                ImGui.SetClipboardText(email);
            SetTooltip(Loc.Localize("Contact.Copy", "Click to copy."));
        }
        ImGui.EndGroup();
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
