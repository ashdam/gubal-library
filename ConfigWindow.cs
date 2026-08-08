using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace GubalLibrary;

internal sealed class ConfigWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);

    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<StatusSnapshot> status;
    private readonly Func<PageStatus> pageStatus;

    /// <param name="version">
    ///     The plugin's own version, shown in the title bar.
    /// </param>
    /// <remarks>
    ///     The window id is pinned with <c>###</c> so the title can carry the version without ImGui
    ///     treating each build as a different window and forgetting its size and position.
    /// </remarks>
    public ConfigWindow(
        Configuration config,
        Action<Configuration> save,
        Func<StatusSnapshot> status,
        FileDialogManager fileDialogs,
        Func<PageStatus> pageStatus,
        string version)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.config = config;
        this.save = save;
        this.status = status;
        this.fileDialogs = fileDialogs;
        this.pageStatus = pageStatus;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(900, 800),
        };
    }

    public Action? OnReloadRequested { get; set; }

    /// <summary>
    ///     The first thing in the window: whether translated pages are actually reaching the game.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Recomputed every frame rather than cached, because the interesting part of it changes
    ///         while the window is open. It reports two different things on purpose: how many pages
    ///         are registered, and how many reads have been <em>answered</em> from them. Those are not
    ///         the same number and confusing them cost a whole session — a route that is installed but
    ///         has served nothing looks, from every other indicator, exactly like one that is working.
    ///     </para>
    ///     <para>
    ///         What used to be here was a Penumbra status line. It went with the dependency: the
    ///         redirection now happens inside this plugin, so there is nothing left to check for.
    ///     </para>
    /// </remarks>
    private void DrawPageStatus()
    {
        var pages = this.pageStatus();
        var pack = pages.Manifest;

        var (colour, icon, text) = pages switch
        {
            { Active: true, ServedCount: > 0 } => (
                Green,
                FontAwesomeIcon.Check,
                $"{pack!.DisplayName} ({pack.TranslationVersion ?? "unversioned"})"),

            // Loaded and never hit. Amber rather than green: at the title screen it is simply too
            // early, but a few minutes into a session it means the redirection is not being reached.
            { Active: true } => (
                Amber,
                FontAwesomeIcon.Check,
                $"{pack!.DisplayName} ({pack.TranslationVersion ?? "unversioned"}) — loaded, nothing read yet."),

            { Error: { Length: > 0 } error } => (
                Red,
                FontAwesomeIcon.ExclamationTriangle,
                error),

            _ => (
                Amber,
                FontAwesomeIcon.ExclamationTriangle,
                "NO LANGUAGE PACK LOADED. Point at a page folder below and restart the client."),
        };

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        ImGui.SameLine();
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();

        if (pack is null)
        {
            return;
        }

        var by = pack.Author is { Length: > 0 } author ? $" by {author}" : string.Empty;
        ImGui.TextDisabled($"{pack.LanguageName ?? pack.Language ?? "unknown language"}{by}");

        // Reported as a fraction with its denominator named, not as a bare percentage. The
        // denominator counts only sheets the corpus has opened at all, so a sheet nobody has started
        // on is missing from both sides and the ratio flatters the pack — "of the game" would be a
        // materially different and much smaller number.
        if (pack.Rows > 0)
        {
            ImGui.TextDisabled(
                $"{pack.Lines:N0} of {pack.Rows:N0} lines translated ({pack.TranslatedFraction:P1}) "
                + $"across {pack.Pages:N0} page(s), in the sheets the corpus covers");
        }

        ImGui.TextDisabled(
            $"Built for game {pack.GameVersion ?? "unknown"}"
            + (pack.CorpusCommit is { Length: > 0 } commit ? $", corpus {commit}" : string.Empty));

        if (pages.Active)
        {
            ImGui.TextDisabled($"{pages.ServedCount:N0} read(s) answered from disk this session");
        }

        // Not the version check — that one refuses outright and never gets this far. This is the
        // quieter drift: pages that rebuild cleanly against today's patch, carrying translations
        // delivered against an older one, which may describe text the game has since changed. No
        // version comparison can see it, because both halves are individually consistent.
        if (pack.OlderCorpusVersions is { Count: > 0 } older)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            using (ImRaii.PushFont(UiBuilder.IconFont, true))
            {
                ImGui.TextUnformatted(FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }

            ImGui.SameLine();
            ImGui.TextWrapped(
                $"Some translations were delivered against {string.Join(", ", older)} rather than "
                + $"{pack.GameVersion}. They may describe text the game has changed since.");
            ImGui.PopStyleColor();
        }
    }

    public override void Draw()
    {
        var snapshot = this.status();

        this.DrawPageStatus();
        ImGui.Separator();

        ImGui.TextUnformatted($"Entries loaded: {snapshot.EntryCount}");
        ImGui.TextUnformatted($"NPC names:      {snapshot.NpcNameCount}");
        // Both handlers, itemised. One number here used to mean TalkHandler alone, so every subtitle,
        // balloon, battle banner and duty description injected counted as zero — and that is not a
        // cosmetic undercount: it was read as evidence that an overlay had not injected at all.
        ImGui.TextUnformatted(
            $"Lines injected: {snapshot.InjectedCount} dialogue + {snapshot.OverlayInjectedCount} overlay");
        ImGui.TextUnformatted($"Misses seen:    {snapshot.MissCount}");
        ImGui.Spacing();
        ImGui.TextWrapped($"Source: {snapshot.LoadedFrom}");

        // "Entries loaded: 0" before a character exists is expected, not a fault: keys are built by
        // resolving macros against live game state, so the corpus waits for someone to build against.
        // Saying nothing here would look identical to a corpus that failed to load.
        if (snapshot.EntryCount == 0 && !snapshot.UsingSampleCorpus)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "Nothing loaded yet. The language pack is indexed against the logged-in character, so it "
                + "loads when you enter the world — not at the title screen.");
        }

        // Loud on purpose, and worded as "no corpus" rather than "sample corpus". The two Ahldskyf
        // lines are a smoke test that tells an empty install apart from a broken one; calling them a
        // corpus here would imply the plugin came with something, which it did not.
        if (snapshot.UsingSampleCorpus)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Red);
            ImGui.TextWrapped(
                "NO LANGUAGE PACK LOADED — running on the built-in self-test, which covers two lines from "
                + "Ahldskyf in Limsa Lominsa Lower Decks and nothing else. The rest of the game is "
                + "untranslated. Use Browse below to load a real language pack.");
            ImGui.PopStyleColor();
        }

        ImGui.Separator();

        var changed = false;

        // "Inject text", not "Enabled". It only ever governed injection, but when that was the only
        // route the distinction did not exist; now that pages are served as files as well, a box
        // labelled Enabled that leaves half the plugin running is a trap.
        var enabled = this.config.Enabled;
        if (ImGui.Checkbox("Inject text into the UI", ref enabled))
        {
            this.config.Enabled = enabled;
            changed = true;
        }

        SetTooltip("The original route: intercept what the game is about to draw and swap it.\n"
                   + "Independent of the page redirection above — turning this off does not stop that.");

        var translateNames = this.config.TranslateNpcNames;
        if (ImGui.Checkbox("Translate speaker names", ref translateNames))
        {
            this.config.TranslateNpcNames = translateNames;
            changed = true;
        }

        SetTooltip("Most NPC names are proper nouns, so this is off by default.");

        var logMisses = this.config.LogMisses;
        if (ImGui.Checkbox("Log untranslated lines", ref logMisses))
        {
            this.config.LogMisses = logMisses;
            changed = true;
        }

        SetTooltip($"Appends the normalized lookup key of each unmatched line to:\n{snapshot.MissLogPath}");

        var probeEvents = this.config.ProbeEvents;
        if (ImGui.Checkbox("Log event handler per line (debug)", ref probeEvents))
        {
            this.config.ProbeEvents = probeEvents;
            changed = true;
        }

        SetTooltip("Reports which quest the game thinks is running, and the conversation the lookup\n" +
                   "is scoped to. Use it when a line stays English and you want to know whether the\n" +
                   "scoping or the language pack is at fault.");

        ImGui.Separator();

        // The corpus is not shipped with the plugin, so it has to be findable. Blank means the
        // plugin config directory.
        var corpusPath = this.config.CorpusPath;

        // Aligned to the frame padding, not drawn at the raw cursor: text placed beside an input box
        // sits at the top of it otherwise, a couple of pixels above the text inside the box.
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Language pack");
        ImGui.SameLine();

        // Negative width, so the box gives back a fixed strip to Browse and Clear on its right and
        // takes whatever is left of the row after the label. Both ends stay put as the window resizes.
        ImGui.SetNextItemWidth(-140f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##corpusPath", ref corpusPath, 1024))
        {
            this.config.CorpusPath = corpusPath;
            changed = true;
        }

        SetTooltip("Absolute path to the language pack JSON.\n" +
                   "Leave empty to use corpus.json in the plugin config directory:\n" +
                   snapshot.ConfigDirectory);

        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            this.BrowseForCorpus();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            this.config.CorpusPath = string.Empty;
            changed = true;
        }

        SetTooltip("Fall back to the plugin config directory.");

        this.DrawPagesRow(ref changed);

        // Directly under the row that loads the pack, because it is the answer to what that row just
        // did. Up beside the entry counts it read as a statistic about the plugin rather than as the
        // identity of the file in the box above it. Green so a reload is visibly confirmed at a glance:
        // the file is regenerated in place several times a session and its name never changes, so this
        // string is the only thing on screen that differs between the old pack and the new one.
        // Green only when there is a version to show. Painting "not stated" green would give an
        // unstamped pack the same reassuring colour as a confirmed one, when it is the case where the
        // question cannot be answered at all.
        if (snapshot.TranslationVersion is { } version)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.85f, 0.4f, 1f));
            ImGui.TextWrapped($"Pack version: {version}");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextDisabled("Pack version: not stated — this pack predates the stamp");
        }

        if (ImGui.Button("Reload translation file"))
        {
            this.OnReloadRequested?.Invoke();
        }

        if (changed)
        {
            this.save(this.config);
        }
    }

    /// <summary>
    ///     Opens the file picker, starting in the folder of the currently configured corpus.
    /// </summary>
    /// <remarks>
    ///     The callback runs on the UI thread and triggers a reload. For a large corpus that is a
    ///     visible hitch, but it follows an explicit user action, and picking a file without it
    ///     appearing to do anything would be worse.
    /// </remarks>
    /// <summary>
    ///     The folder of rebuilt <c>.exd</c> pages, and the switch that serves it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A folder rather than a file, and not bundled, for the same reasons as the language
    ///         pack: thousands of files, tens of megabytes, regenerated on the game's cadence rather
    ///         than the code's.
    ///     </para>
    ///     <para>
    ///         A checkbox rather than a Serve button, and it says so, because there is nothing this
    ///         can do now. The game reads its sheets once, seconds into startup, and caches them for
    ///         the session; the redirection has to be in place before that or it may as well not
    ///         exist. So this decides what happens at the <em>next</em> start, and a button labelled
    ///         Serve that changed nothing visible was read — correctly — as a broken button.
    ///     </para>
    /// </remarks>
    private void DrawPagesRow(ref bool changed)
    {
        var pagesPath = this.config.PagesPath;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Page folder ");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(-80f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##pagesPath", ref pagesPath, 1024))
        {
            this.config.PagesPath = pagesPath;
            changed = true;
        }

        SetTooltip("Folder of rebuilt .exd pages, as written by Tools\\ExdRedirect.\n"
                   + "Must contain gubal-manifest.json.");

        ImGui.SameLine();
        if (ImGui.Button("Browse...##pages"))
        {
            this.BrowseForPages();
        }

        var serve = this.config.ServePages;
        using (ImRaii.Disabled(this.config.PagesPath.Length == 0))
        {
            if (ImGui.Checkbox("Serve translated pages from the next start", ref serve))
            {
                this.config.ServePages = serve;
                changed = true;
            }
        }

        SetTooltip("Hands the game the rebuilt pages instead of the ones in its archives.\n"
                   + "Takes effect when the client next starts: sheets are read once at startup\n"
                   + "and kept for the session, so this cannot be switched on mid-game.");
    }

    private void BrowseForPages()
    {
        var startPath = this.config.PagesPath;
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            startPath = string.Empty;
        }

        this.fileDialogs.OpenFolderDialog(
            "Select the page folder",
            (confirmed, selectedPath) =>
            {
                if (!confirmed || string.IsNullOrWhiteSpace(selectedPath))
                {
                    return;
                }

                this.config.PagesPath = selectedPath;
                this.save(this.config);
            },
            startPath);
    }

    private void BrowseForCorpus()
    {
        var startPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(this.config.CorpusPath))
        {
            try
            {
                startPath = Path.GetDirectoryName(this.config.CorpusPath) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                // Malformed path typed by hand; just open wherever the dialog defaults to.
            }
        }

        // The overload that accepts a start path reports results as a list, even with a max of one.
        this.fileDialogs.OpenFileDialog(
            "Select language pack",
            "JSON{.json},All files{.*}",
            (accepted, selectedPaths) =>
            {
                if (!accepted || selectedPaths.Count == 0 || string.IsNullOrWhiteSpace(selectedPaths[0]))
                {
                    return;
                }

                this.config.CorpusPath = selectedPaths[0];
                this.save(this.config);
                this.OnReloadRequested?.Invoke();
            },
            1,
            startPath);
    }

    private static void SetTooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text);
        }
    }
}

internal readonly record struct StatusSnapshot(
    int EntryCount,
    int NpcNameCount,
    int InjectedCount,
    int OverlayInjectedCount,
    int MissCount,
    string LoadedFrom,
    string? TranslationVersion,
    string MissLogPath,
    string ConfigDirectory,
    bool UsingSampleCorpus);

/// <param name="Active">The redirection is installed and holding pages.</param>
/// <param name="PageCount">How many pages it would answer for.</param>
/// <param name="ServedCount">How many reads it has actually answered — the number that proves it.</param>
/// <param name="Error">Why it is not installed, when it is not. Null when it is, or when nobody asked.</param>
/// <param name="Manifest">What the loaded pack says about itself. Null when none loaded.</param>
internal readonly record struct PageStatus(
    bool Active, int PageCount, int ServedCount, string? Error, PackManifest? Manifest);

