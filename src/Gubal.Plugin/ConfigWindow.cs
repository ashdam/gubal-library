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
    public Action? DrawIssueReport { get; set; }
    public bool IsIssueReportingVisible { get; private set; }
    private LibrarySection selectedSection;
    private bool sectionNeedsScroll;
    private bool issueReportNeedsScroll;
    private static readonly string[] CaptureKeys = Enumerable.Range(1, 12).Select(key => "F" + key).ToArray();

    public void OpenIssueReporting()
    {
        this.selectedSection = LibrarySection.Report;
        this.IsOpen = true;
    }
    private static readonly Vector4 Green = ThemeColor(0x7fc9a0);
    private static readonly Vector4 Amber = ThemeColor(0xe0a35c);
    private static readonly Vector4 Red = ThemeColor(0xef8888);
    private static readonly Vector4 Grey = PluginTheme.Muted;

    private static readonly Vector4 Blue = ThemeColor(0x7fc8ef);

    private readonly PluginUi ui;
    private readonly string pluginVersion;
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
        PluginUi ui,
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
        this.ui = ui;
        this.pluginVersion = version;
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
        this.Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            // Keep language cards usable at the minimum width.
            MinimumSize = new Vector2(760, 420),
        };
    }

    public override void Draw()
    {
        this.IsIssueReportingVisible = false;
        var pages = this.pageStatus();
        var changed = false;

        if (this.operations.HasPending && this.config.ServeLanguagePack)
        {
            this.restartReason = InstalledPackRestartMessage();
        }
        this.DrawLibrary(pages, ref changed);

        if (changed)
        {
            this.save(this.config);
        }
    }

    private void DrawSetupTab(PageStatus pages, ref bool changed)
    {
        this.DrawPageHeading(Loc.Localize("Tab.Pack", "Language pack"),
            Loc.Localize("Library.ChoosePack", "Choose your language pack for FINAL FANTASY XIV."), this.DrawLanguageContact);
        this.DrawCurrentPack(pages);
        this.DrawLanguagePackRow(pages, ref changed);
    }

    private void DrawPartsTab(ref bool changed)
    {
        this.DrawPageHeading(Loc.Localize("Tab.Parts", "Localization settings"),
            Loc.Localize("Library.LocalizationIntro", "Choose what is translated and how extra information is shown."));
        this.DrawInGameFeatures(ref changed);
        ImGui.Spacing();

        var pack = this.contents();

        var height = Math.Max(200f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().Y - this.CompatibilityHeight() - ImGui.GetStyle().ItemSpacing.Y * 3);
        using (var gameText = ImRaii.Child("##gameTextSettings", new Vector2(0, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (gameText)
            {
                if (pack.Layout.Count == 0)
                {
                    PluginUi.DrawSubheading(Loc.Localize("Library.GameText", "Game text"));
                    ImGui.TextWrapped(Loc.Localize("Parts.Empty", "No language pack installed."));
                }
                else
                {
                    this.DrawPartsHeader(pack, ref changed);
                    this.DrawPartSelection(pack, ref changed);
                }
            }
        }
        ImGui.Spacing();
        this.DrawCompatibility();
    }

    private void DrawFeaturePreview(string description, string? imageName, float centerY, Vector2? uv0 = null, Vector2? uv1 = null)
    {
        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var icon = FontAwesomeIcon.InfoCircle.ToIconString();
            var ink = PluginUi.TextInkBounds(icon);
            var position = new Vector2(ImGui.GetCursorScreenPos().X, centerY - (ink.Bottom - ink.Top) / 2);
            ImGui.GetWindowDrawList().AddText(position - new Vector2(0, ink.Top), ImGui.GetColorU32(Grey), icon);
            ImGui.SetCursorScreenPos(position);
            ImGui.Dummy(new Vector2(ImGui.CalcTextSize(icon).X, ink.Bottom - ink.Top));
        }
        if (!ImGui.IsItemHovered()) return;
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted(description);
        if (imageName == null || this.Picture(imageName) is not { } picture) return;
        var start = uv0 ?? Vector2.Zero;
        var end = uv1 ?? Vector2.One;
        var crop = new Vector2(picture.Width, picture.Height) * (end - start);
        var width = Math.Min(600f * ImGuiHelpers.GlobalScale, crop.X);
        ImGui.Spacing();
        ImGui.Image(picture.Handle, new Vector2(width, width * crop.Y / crop.X), start, end);
    }

    private void DrawIssueReportingTab(ref bool changed)
    {
        this.DrawPageHeading(Loc.Localize("Tab.IssueReporting", "Report an issue"),
            Loc.Localize("Report.Intro", "Capture the text and describe what needs to change."));
        var shortcutLabel = Loc.Localize("Report.Key", "Capture shortcut");
        var controlsWidth = ImGui.CalcTextSize("CtrlShiftAlt").X + ImGui.GetFrameHeight() * 3
            + ImGui.GetStyle().ItemSpacing.X * 6 + PluginUi.ComboBoxWidth(CaptureKeys);
        var inline = ImGui.CalcTextSize(shortcutLabel).X + controlsWidth + ImGui.GetStyle().ItemSpacing.X
            <= ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X * 2;
        var controlsHeight = ImGui.GetFrameHeight() * (inline ? 1 : 2) + (inline ? 0 : ImGui.GetStyle().ItemSpacing.Y);
        var optionsHeight = controlsHeight + ImGui.GetStyle().WindowPadding.Y * 2;
        using (var options = ImRaii.Child("##issueReportOptions", new Vector2(0, optionsHeight), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (options)
            {
                ImGui.SetCursorPosY((ImGui.GetWindowSize().Y - controlsHeight) / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(shortcutLabel);
                if (inline)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - controlsWidth));
                }
                var ctrl = this.config.ReportHotkeyCtrl;
                if (PluginUi.Checkbox("Ctrl", ref ctrl)) { this.config.ReportHotkeyCtrl = ctrl; changed = true; }
                ImGui.SameLine();
                var shift = this.config.ReportHotkeyShift;
                if (PluginUi.Checkbox("Shift", ref shift)) { this.config.ReportHotkeyShift = shift; changed = true; }
                ImGui.SameLine();
                var alt = this.config.ReportHotkeyAlt;
                if (PluginUi.Checkbox("Alt", ref alt)) { this.config.ReportHotkeyAlt = alt; changed = true; }
                ImGui.SameLine();
                var function = Math.Clamp(this.config.ReportHotkeyFunction, 1, 12) - 1;
                ImGui.SetNextItemWidth(PluginUi.ComboBoxWidth(CaptureKeys));
                if (PluginUi.ComboBox("##reportShortcutKey", ref function, CaptureKeys))
                { this.config.ReportHotkeyFunction = function + 1; changed = true; }
            }
        }
        ImGui.Spacing();
        var hideScrollbar = !this.issueReportNeedsScroll;
        if (hideScrollbar)
        {
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Vector4.Zero);
        }
        using var report = ImRaii.Child("##issueReportContent", new Vector2(0, 0), false,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);
        if (hideScrollbar) ImGui.PopStyleColor(4);
        if (report)
        {
            this.issueReportNeedsScroll = ImGui.GetScrollMaxY() > 0;
            this.DrawIssueReport?.Invoke();
        }
    }

    private float CompatibilityHeight()
    {
        var titleHeight = PluginUi.SubheadingSize(Loc.Localize("Compatibility.Title", "Compatibility")).Y + ImGui.GetStyle().ItemSpacing.Y;
        return titleHeight + ImGui.GetTextLineHeightWithSpacing() * 2 + ImGui.GetStyle().WindowPadding.Y * 2
            + ImGui.GetStyle().ItemSpacing.Y * 2 + 1;
    }

    private void DrawCompatibility()
    {
        using var compatibility = ImRaii.Child("##pluginCompatibility", new Vector2(0, this.CompatibilityHeight()), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!compatibility) return;
        PluginUi.DrawSubheading(Loc.Localize("Compatibility.Title", "Compatibility"));
        ImGui.Separator();
        DrawSupportedPlugin("Lifestream", "2.6.0.11");
        DrawSupportedPlugin("AutoRetainer", "4.6.2.11");
    }

    private static void DrawSupportedPlugin(string name, string version)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
            ImGui.TextColored(Green, FontAwesomeIcon.Check.ToIconString());
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(Loc.Localize("Library.CompatibleVersion", "{0} or later"), version));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(string.Format(Loc.Localize("Compatibility.Since",
                "Compatible since version {0}."), version));
    }

    private void DrawPartsHeader(PackContents pack, ref bool changed)
    {
        var total = pack.PartCount;
        var off = pack.PartsOff(this.config.DisabledSheets).Count;
        var title = Loc.Localize("Library.GameText", "Game text");
        var count = string.Format(Loc.Localize("Parts.Count", "{0} of {1} ON"), total - off, total);
        var titleSize = PluginUi.SubheadingSize(title);
        var reset = Loc.Localize("Parts.TurnAllOn", "Turn everything ON");
        var disable = Loc.Localize("Parts.TurnAllOff", "Turn everything OFF");
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var width = PluginUi.MeasureButton(reset).X + PluginUi.MeasureButton(disable).X + gap;
        var small = titleSize.X + ImGui.CalcTextSize(count).X + width + gap * 4 > ImGui.GetContentRegionAvail().X;
        if (small) width = PluginUi.MeasureButton(reset, true).X + PluginUi.MeasureButton(disable, true).X + gap;
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero))
        using (var header = ImRaii.Table("##gameTextHeader", 3,
                   ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings))
        {
            if (header)
            {
                ImGui.TableSetupColumn("##title", ImGuiTableColumnFlags.WidthFixed, titleSize.X + gap * 2);
                ImGui.TableSetupColumn("##count", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, width);
                ImGui.TableNextColumn();
                var top = ImGui.GetCursorPosY();
                var height = Math.Max(ImGui.GetFrameHeight(), titleSize.Y);
                ImGui.SetCursorPosY(top + (height - titleSize.Y) / 2);
                PluginUi.DrawSubheading(title);
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(top + (height - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextDisabled(count);
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(top + (height - ImGui.GetFrameHeight()) / 2);
                using (ImRaii.Disabled(off == 0))
                {
                    if (PluginUi.Button(reset, small))
                    {
                        this.config.DisabledSheets.Clear();
                        this.NoteParted();
                        changed = true;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(off == total))
                {
                    if (PluginUi.Button(disable, small))
                    {
                        this.SetSheets(pack.Layout.SelectMany(g => g.Parts).SelectMany(p => p.Sheets), false);
                        changed = true;
                    }
                }
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

        this.DrawUpdateNotice(pages);

        if (!pages.Active || pages.Manifest is null)
        {
            if (this.operations.HasPending && this.config.ServeLanguagePack) return;
            if (this.HasInactivePack(pages)) return;
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
        if (pages.Manifest is null || !PackInstaller.IsRemote(pages.Source ?? this.config.InstalledFrom))
        {
            return;
        }

        var (icon, colour, message) = pages.Update.State switch
        {
            UpdateState.Checking => (FontAwesomeIcon.Hourglass, Grey,
                Loc.Localize("Update.Checking", "Checking whether a newer language pack is published...")),
            UpdateState.Unreachable => (FontAwesomeIcon.ExclamationTriangle, Red,
                Loc.Localize("Update.Unreachable", "No connection to the language pack update URL. If it persists, this pack "
                    + "will not update itself, so check where you got it from.")),
            UpdateState.NotDeclared => (FontAwesomeIcon.ExclamationTriangle, Amber,
                Loc.Localize("Update.NotDeclared", "This language pack has no update URL, so it will never update itself.")),
            _ => (default, default, string.Empty),
        };

        var height = ImGui.GetTextLineHeight();
        if (message.Length == 0 || this.restartReason is not null)
        {
            ImGui.Dummy(new Vector2(0, height));
            return;
        }

        Icon(icon, colour);
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), height);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(start, start + size, true);
        draw.AddText(start, ImGui.GetColorU32(colour), message);
        draw.PopClipRect();
        ImGui.PopStyleColor();
        ImGui.Dummy(size);
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(message).X > size.X) SetTooltip(message);
    }

    private bool HasInactivePack(PageStatus pages) => (!pages.Active || pages.Manifest is null)
        && string.IsNullOrWhiteSpace(pages.Error) && this.config.ServeLanguagePack && this.config.LanguagePackPath.Length > 0;

    private void DrawRestartBanner(float contentX, float centerY, float controlsWidth)
    {
        var pages = this.pageStatus();
        var cacheNotice = this.shadowState()?.RestartNotice;
        var inactivePack = this.HasInactivePack(pages);
        var pendingPack = this.operations.HasPending && this.config.ServeLanguagePack;
        var needsSetup = cacheNotice is null && pendingPack && this.dalamudWaits() is not true;
        if (cacheNotice is null && !needsSetup && !inactivePack && string.IsNullOrWhiteSpace(this.restartReason)) return;
        var reason = this.restartReason ?? Loc.Localize("Pack.Pending",
            "A language pack is installed but not in use yet. Restart the game.");
        var label = cacheNotice ?? (needsSetup ? Loc.Localize("Setup.WaitRequired", "Setup required")
            : pendingPack || inactivePack ? reason : Loc.Localize("Restart.Title", "RESTART THE GAME"));
        var icon = (needsSetup ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.PowerOff).ToIconString();
        var colour = cacheNotice is not null ? Red : Amber;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetCursorScreenPos(new Vector2(contentX, centerY - ImGui.GetTextLineHeight() / 2));
        using (ImRaii.Group())
        using (ImRaii.PushColor(ImGuiCol.Text, colour))
        {
            float iconWidth;
            using (ImRaii.PushFont(UiBuilder.IconFont, true))
            {
                iconWidth = ImGui.CalcTextSize(icon).X;
                ImGui.SetCursorScreenPos(new Vector2(contentX,
                    centerY - ImGui.GetTextLineHeight() / 2));
                DrawHeaderText(icon, centerY, colour);
            }
            ImGui.SetCursorScreenPos(new Vector2(contentX + iconWidth + spacing, centerY - ImGui.GetTextLineHeight() / 2));
            var textWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - controlsWidth - spacing);
            DrawCenteredWrappedText(label, centerY, textWidth);
        }
        if (needsSetup && ImGui.IsItemClicked()) this.openDalamudSettings();
        if (cacheNotice is not null || !ImGui.IsItemHovered()) return;
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(400f * ImGuiHelpers.GlobalScale);
        if (needsSetup)
            ImGui.TextWrapped(Loc.Localize("Boot.NotWaiting",
                "Enable \"Wait for plugins before game loads\" in Dalamud's General settings to allow language pack activation and startup updates."));
        else
        {
            if (!pendingPack) ImGui.TextWrapped(reason);
            var current = this.pageStatus();
            ImGui.TextWrapped(current.Active && current.Manifest is { } manifest
                ? string.Format(Loc.Localize("Library.SessionUnchanged", "{0} remains active in this session."), manifest.LanguageName ?? manifest.DisplayName)
                : Loc.Localize("Library.EnglishSession", "The game still uses English in this session."));
        }
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
        PluginUi.DrawSubheading(pages.Active ? Loc.Localize("Library.OtherLanguages", "Other languages")
            : Loc.Localize("Library.AvailableLanguages", "Available languages"));
        this.DrawLanguageCards(pages, ref changed);
        ImGui.Spacing();
        if (PluginUi.Disclosure(Loc.Localize("Library.LocalPack", "Use a local pack")))
        {
            this.DrawLocalPackSettings(pages, ref changed);
        }
        ImGui.Spacing();
        this.DrawAutoUpdateRow();

        if (!this.operations.Installing && this.installMessage is { Length: > 0 } message)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, this.installFailed ? Red : Green);
            ImGui.TextWrapped(message);
            ImGui.PopStyleColor();
        }
    }

    private void DrawLanguageName(string? flagCode, FontAwesomeIcon? glyph, string label, bool prominent = false, string? site = null)
    {
        using var font = prominent ? this.ui.HeadingFont.Push() : this.ui.ActionFont.Push();
        using var color = ImRaii.PushColor(ImGuiCol.Text, ThemeGold, prominent);
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
            var rowHeight = ImGui.GetFrameHeight();
            using (ImRaii.PushFont(UiBuilder.IconFont, true))
            {
                ImGui.SetCursorPosY(rowY + Math.Max(0, (rowHeight - ImGui.GetTextLineHeight()) / 2));
                ImGui.TextUnformatted(icon.ToIconString());
            }
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
        }
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, Blue, !string.IsNullOrWhiteSpace(site)))
            ImGui.TextWrapped(label);
        if (!string.IsNullOrWhiteSpace(site)) DrawLinkInteraction(() => Util.OpenLink(site), site);
    }

    // The manifest published at a pack's release, fetched once per session.

    private (PackManifest? Manifest, bool Loading) PublishedManifest(KnownPack pack) =>
        this.operations.Published(pack);

    private static bool SameSource(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool IsInstalledOfficialPack(KnownPack pack, PageStatus pages) =>
        SameSource(pack.Source, pages.Active ? pages.Source : this.config.InstalledFrom);

    private void DrawOfficialPackAction(KnownPack pack, PackManifest? published, PageStatus pages, bool unavailable)
    {
        var installed = this.IsInstalledOfficialPack(pack, pages);
        if (installed && pages.Update is { State: UpdateState.Available, Published: { } update }) published = update;
        var moved = installed && !SameSource(pack.Source, this.config.InstalledFrom);
        var newer = installed && pages.Manifest is { } current && published is not null
            && (string.CompareOrdinal(published.TranslationVersion, current.TranslationVersion) > 0
                || published.GameVersion == this.RunningGame() && current.GameVersion != published.GameVersion);
        var isUpdate = newer || moved;
        var label = isUpdate ? Loc.Localize("Setup.Update", "Update") : Loc.Localize("Setup.Install", "Install");
        var nextVersion = isUpdate ? published?.TranslationVersion : null;
        var extraWidth = isUpdate ? 0 : InstallButtonExtraWidth;
        if (this.DrawPackActionState(pack.Source, installed && pages.Active && !isUpdate, label, published, nextVersion, extraWidth)) return;
        if (!pack.Published)
        {
            var noPack = Loc.Localize("Setup.NoPackYet", "No pack yet");
            AlignPackAction(noPack);
            ImGui.TextDisabled(noPack);
            return;
        }
        using var disabled = ImRaii.Disabled(this.operations.Installing || unavailable || pack.Source is null);
        this.DrawPackSizes(label, published, nextVersion, extraWidth: extraWidth);
        if (this.ui.DrawActionButton(label, true, detail: nextVersion, extraWidth: extraWidth))
        {
            this.RequestPackChange(pack.Source!, pack.Name, published);
        }
        if (ImGui.IsItemHovered()) SetTooltip(Loc.Localize("Setup.InstallTip", "Prepare this language pack for the next game start."));
    }

    private static string PackRestartLabel() =>
        Loc.Localize("Library.Ready", "Please restart the game to apply it.");

    private static string InstalledPackRestartMessage() =>
        string.Format(Loc.Localize("Restart.Installed", "You installed a new language pack. {0}"), PackRestartLabel());

    private static void DrawCenteredWrappedText(string text, float centerY, float width)
    {
        var position = ImGui.GetCursorScreenPos();
        position.Y = centerY - ImGui.CalcTextSize(text, false, width).Y / 2;
        ImGui.SetCursorScreenPos(position);
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextUnformatted(text);
    }

    private static void DrawPackActionNotice(string text, Vector2 slot)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;

        var width = slot.X + ImGui.GetStyle().WindowPadding.X / 2;
        var textHeight = ImGui.GetTextLineHeight();
        var lines = new List<string>(words.Length);
        var scale = 1f;
        while (true)
        {
            lines.Clear();
            var line = words[0];
            for (var i = 1; i < words.Length; i++)
            {
                var next = $"{line} {words[i]}";
                if (ImGui.CalcTextSize(next).X * scale <= width) line = next;
                else
                {
                    lines.Add(line);
                    line = words[i];
                }
            }
            lines.Add(line);

            var widest = lines.Max(value => ImGui.CalcTextSize(value).X);
            var fit = Math.Min(width / widest, slot.Y / (lines.Count * textHeight));
            if (scale <= fit) break;
            if (scale <= 0.5f)
            {
                scale = fit;
                break;
            }
            scale -= 0.05f;
        }

        var lineHeight = textHeight * scale;
        var start = ImGui.GetCursorScreenPos();
        var y = start.Y + (slot.Y - lines.Count * lineHeight) / 2;
        var draw = ImGui.GetWindowDrawList();
        foreach (var value in lines)
        {
            var x = start.X + (width - ImGui.CalcTextSize(value).X * scale) / 2;
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(x, y), ImGui.GetColorU32(Amber), value);
            y += lineHeight;
        }
        ImGui.Dummy(slot);
    }

    private bool DrawPackActionState(string? source, bool active, string action, PackManifest? manifest,
        string? detail = null, float extraWidth = 0)
    {
        if (SameSource(source, this.operations.InstallingSource))
        {
            this.DrawInstallProgress();
            return true;
        }
        if (this.config.ServeLanguagePack && SameSource(source, this.operations.PendingSource))
        {
            var canRestart = this.dalamudWaits() is true;
            var label = canRestart
                ? PackRestartLabel()
                : Loc.Localize("Setup.WaitRequired", "Setup required");
            var slot = this.DrawPackSizes(action, manifest, detail, isNotice: true, extraWidth: extraWidth);
            DrawPackActionNotice(label, slot);
            return true;
        }
        ImGui.AlignTextToFramePadding();
        if (this.config.ServeLanguagePack && active)
        {
            this.DrawPackSizes(Loc.Localize("Setup.Active", "Active"), manifest);
            this.DrawPackActiveBadge();
            return true;
        }
        return false;
    }

    private void DrawInstallProgress()
    {
        var progress = this.operations.Progress;
        PluginUi.ProgressBar(progress.Fraction ?? -1f * (float)ImGui.GetTime(),
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
        if (PluginUi.Checkbox(Loc.Localize("Setup.Auto", "Auto-update at start"), ref auto))
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

        if (PluginUi.Button($"{Loc.Localize("Boot.OpenSettings", "Open Dalamud settings")}##bootWait"))
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
            this.restartReason = InstalledPackRestartMessage();
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

        using (ImRaii.PushColor(ImGuiCol.Text, Grey)) ImGui.TextWrapped(detail);

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
        this.DrawPageHeading(Loc.Localize("Tab.Help", "Help and contact"),
            Loc.Localize("Library.HelpIntro", "Find the right place for your question."));
        PluginUi.DrawSubheading(Loc.Localize("Library.TranslationProblem", "A translation is incorrect"));
        ImGui.TextWrapped(Loc.Localize("Library.TranslationHelp", "Report incorrect text, missing text, or an unclear translation. Include a screenshot."));
        Link(Loc.Localize("Tab.IssueReporting", "Report an issue"), this.OpenIssueReporting);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        PluginUi.DrawSubheading(Loc.Localize("Library.PluginProblem", "Gubal Library does not work"));
        ImGui.TextWrapped(Loc.Localize("Help.PluginBugs",
            "Use this link for problems with the Gubal Library plugin. Do not use it for translation errors."));
        Link(Loc.Localize("Help.ReportBug", "Report a bug"), KnownPacks.PluginIssues);
        ImGui.Spacing();
        PluginUi.DrawSubheading(Loc.Localize("Library.Diagnostics", "Information for a plugin bug report"));
        this.DrawBugReportInformation();
        ImGui.Spacing();
        PluginUi.DrawSubheading(Loc.Localize("Library.ReviewerContact", "Contact the reviewers"));
        using (var contacts = ImRaii.Table("##reviewerContacts", 2, ImGuiTableFlags.SizingStretchProp))
        {
            if (contacts)
            {
                ImGui.TableSetupColumn("##language", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##email", ImGuiTableColumnFlags.WidthStretch);
                foreach (var contact in KnownPacks.ReviewerContacts)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(contact.Language);
                    ImGui.TableNextColumn();
                    DrawEmailContact(contact.Email);
                }
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Link(Loc.Localize("Library.ProjectSite", "Visit the project"), "https://eorzealocalized.com/");
        Link(Loc.Localize("Setup.HelpTranslate", "Help us translate"), "https://eorzealocalized.com/localize/en");
        if (PluginUi.Disclosure(Loc.Localize("Library.Advanced", "Advanced")))
        {
            var dev = this.DevToolsEnabled;
            if (PluginUi.Checkbox(Loc.Localize("Library.ShowDeveloper", "Show developer tools"), ref dev)) this.ToggleDevTab();
        }
    }

    private static void DrawEmailContact(string email, bool showIcon = false)
    {
        if (showIcon)
        {
            Icon(FontAwesomeIcon.Envelope, Grey);
            ImGui.PopStyleColor();
        }
        Link(email, () => ImGui.SetClipboardText(email), Loc.Localize("Contact.Copy", "Click to copy it."));
    }

    private static void Link(string label, string url) => Link(label, () => Util.OpenLink(url), url);

    private static void Link(string label, Action action, string? tooltip = null)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Blue))
        {
            ImGui.TextUnformatted(label);
        }

        DrawLinkInteraction(action, tooltip);
    }

    private static void DrawLinkInteraction(Action action, string? tooltip)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList()
            .AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), ImGui.GetColorU32(Blue));

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (tooltip is not null) SetTooltip(tooltip);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            action();
        }
    }

    private void BrowseForLanguagePack()
    {
        var startPath = string.IsNullOrWhiteSpace(this.config.OwnPackFolder) ? this.config.LanguagePackPath : this.config.OwnPackFolder;
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath)) startPath = string.Empty;
        this.fileDialogs.OpenFolderDialog(
            Loc.Localize("Setup.PickerTitle", "Select the folder your language pack was built into"),
            (confirmed, selected) =>
            {
                if (!confirmed || string.IsNullOrWhiteSpace(selected)) return;
                this.config.OwnPackFolder = selected;
                this.save(this.config);
            },
            startPath, isModal: false);
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
    CompatibilitySelection? Compatibility = null,
    string? Source = null,
    string? PackPath = null);
