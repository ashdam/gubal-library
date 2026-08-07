using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace GubalLibrary;

internal sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<StatusSnapshot> status;

    public ConfigWindow(
        Configuration config,
        Action<Configuration> save,
        Func<StatusSnapshot> status,
        FileDialogManager fileDialogs)
        : base("Gubal Library###GubalLibraryConfig")
    {
        this.config = config;
        this.save = save;
        this.status = status;
        this.fileDialogs = fileDialogs;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(900, 800),
        };
    }

    public Action? OnReloadRequested { get; set; }

    public override void Draw()
    {
        var snapshot = this.status();

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

        var enabled = this.config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            this.config.Enabled = enabled;
            changed = true;
        }

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
