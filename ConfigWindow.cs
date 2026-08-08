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
    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<StatusSnapshot> status;
    private readonly PenumbraBridge penumbra;

    private string? pageMessage;
    private bool pageMessageIsError;

    public ConfigWindow(
        Configuration config,
        Action<Configuration> save,
        Func<StatusSnapshot> status,
        FileDialogManager fileDialogs,
        PenumbraBridge penumbra)
        : base("Gubal Library###GubalLibraryConfig")
    {
        this.config = config;
        this.save = save;
        this.status = status;
        this.fileDialogs = fileDialogs;
        this.penumbra = penumbra;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(900, 800),
        };
    }

    public Action? OnReloadRequested { get; set; }

    /// <summary>
    ///     The first thing in the window: whether the dependency this route needs is actually there.
    /// </summary>
    /// <remarks>
    ///     Penumbra is what performs the redirection; this plugin only asks it to. Without it the
    ///     pages are inert, and every symptom of that looks exactly like an untranslated game. So the
    ///     answer goes at the top and is recomputed every frame — the user can install or disable
    ///     Penumbra without touching this plugin, and a status line that lies is worse than none.
    /// </remarks>
    private void DrawPenumbraStatus()
    {
        var penumbraStatus = this.penumbra.Detect();

        var (colour, icon, text) = penumbraStatus switch
        {
            { Installed: false } => (
                new Vector4(1f, 0.35f, 0.35f, 1f),
                FontAwesomeIcon.ExclamationTriangle,
                "Penumbra is not installed. Translated pages cannot be served without it."),

            { Loaded: false } => (
                new Vector4(1f, 0.75f, 0.2f, 1f),
                FontAwesomeIcon.ExclamationTriangle,
                $"Penumbra {penumbraStatus.Version} is installed but not loaded."),

            _ => (
                new Vector4(0.4f, 0.9f, 0.4f, 1f),
                FontAwesomeIcon.Check,
                $"Penumbra ({penumbraStatus.Version})"),
        };

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        ImGui.SameLine();
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();

        if (penumbraStatus.Installed && penumbraStatus.Loaded)
        {
            ImGui.TextUnformatted(penumbraStatus.Registered
                ? "Pages: served through Penumbra."
                : "Pages: not registered.");
        }
    }

    public override void Draw()
    {
        var snapshot = this.status();

        this.DrawPenumbraStatus();
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
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
            ImGui.TextWrapped(
                "NO LANGUAGE PACK LOADED — running on the built-in self-test, which covers two lines from "
                + "Ahldskyf in Limsa Lominsa Lower Decks and nothing else. The rest of the game is "
                + "untranslated. Use Browse below to load a real language pack.");
            ImGui.PopStyleColor();
        }

        ImGui.Separator();

        var changed = false;

        // "Inject text", not "Enabled". It only ever governed injection, but when that was the only
        // route the distinction did not exist; now that pages can be served through Penumbra as well,
        // a box labelled Enabled that leaves half the plugin running is a trap.
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
    ///     The folder of rebuilt <c>.exd</c> pages, and the button that hands them to Penumbra.
    /// </summary>
    /// <remarks>
    ///     A folder rather than a file, and not bundled, for the same reasons as the language pack:
    ///     thousands of files, tens of megabytes, regenerated on the game's cadence rather than the
    ///     code's. Registration is explicit rather than automatic on browse, because it is the point
    ///     at which the manifest is checked against the running game and the user needs to see that
    ///     answer rather than have it happen silently.
    /// </remarks>
    private void DrawPagesRow(ref bool changed)
    {
        var pagesPath = this.config.PagesPath;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Page folder ");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(-140f * ImGuiHelpers.GlobalScale);
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

        ImGui.SameLine();

        var status = this.penumbra.Detect();
        if (status.Registered)
        {
            if (ImGui.Button("Unserve"))
            {
                this.penumbra.Unregister();
                this.config.ServePages = false;
                this.pageMessage = "Pages handed back to the game.";
                this.pageMessageIsError = false;
                changed = true;
            }
        }
        else
        {
            using (ImRaii.Disabled(!status.Installed || !status.Loaded))
            {
                if (ImGui.Button("Serve"))
                {
                    var result = this.penumbra.Register(this.config.PagesPath);
                    this.pageMessageIsError = !result.Success;
                    this.pageMessage = result.Success
                        ? $"Serving {result.PageCount:N0} page(s) built for game {result.GameVersion}. "
                          + "Restart the client to see them: sheets are cached at startup."
                        : result.Error;

                    this.config.ServePages = result.Success;
                    changed = true;
                }
            }
        }

        if (this.pageMessage is { Length: > 0 } message)
        {
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                this.pageMessageIsError ? new Vector4(1f, 0.35f, 0.35f, 1f) : new Vector4(0.4f, 0.9f, 0.4f, 1f));
            ImGui.TextWrapped(message);
            ImGui.PopStyleColor();
        }
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
