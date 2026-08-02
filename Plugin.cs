using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Injects pre-translated Spanish NPC dialogue into the Talk window.
/// </summary>
/// <remarks>
///     Makes no network calls. Translation happens entirely offline in a separate pipeline; this plugin
///     only loads the resulting file and performs a dictionary lookup.
/// </remarks>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gubal";
    private const string MissLogFileName = "misses.jsonl";

    /// <summary>
    ///     Corpus file names looked for in the config directory, in order.
    /// </summary>
    /// <remarks>
    ///     <c>corpus.json</c> is the language-neutral name and the one to use. <c>es.json</c> is kept
    ///     because the Spanish corpus predates the rename and is the only one that exists so far;
    ///     dropping it would silently stop working for everyone already testing.
    /// </remarks>
    private static readonly string[] TranslationFileNames = ["corpus.json", "es.json"];

    /// <summary>The bundled two-line sample, relative to the plugin DLL.</summary>
    private const string SampleCorpusRelativePath = @"Data\sample-corpus.json";

    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly ICommandManager commands;
    private readonly FileDialogManager fileDialogs = new();
    private readonly Configuration config;
    private readonly ConfigWindow configWindow;
    private readonly IPluginLog log;
    private readonly MissLog misses;
    private readonly IPlayerState playerState;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly TranslationStore store;
    private readonly TalkHandler talkHandler;
    private readonly OverlayHandler overlays;
    private readonly AddonFinder finder;
    private readonly WindowSystem windows = new("GubalLibrary");

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IPlayerState playerState,
        IClientState clientState,
        IAddonLifecycle addonLifecycle,
        IChatGui chat,
        ISeStringEvaluator evaluator,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.playerState = playerState;
        this.clientState = clientState;
        this.chat = chat;
        this.log = log;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

        this.store = new TranslationStore(log, evaluator);
        this.store.Load(this.TranslationFileCandidates());

        this.misses = new MissLog(
            log,
            Path.Combine(pluginInterface.GetPluginConfigDirectory(), MissLogFileName));

        this.talkHandler = new TalkHandler(
            addonLifecycle,
            log,
            this.config,
            this.store,
            this.misses,
            evaluator);

        // One handler, several candidate addon names. TalkSubtitle is proven; the others are guesses
        // that cost nothing if they never fire and announce themselves in the log if they do.
        this.overlays = new OverlayHandler(
            addonLifecycle,
            log,
            this.config,
            this.store,
            this.misses,
            "TalkSubtitle",
            "_BattleTalk",
            "_ScreenInfoFront",
            "_MiniTalk",
            "MiniTalk");

        this.finder = new AddonFinder(addonLifecycle, log);
        this.finder.Hunt(this.config.FindText);

        this.configWindow = new ConfigWindow(this.config, this.SaveConfig, this.Snapshot, this.fileDialogs)
        {
            OnReloadRequested = this.ReloadTranslations,
        };
        this.windows.AddWindow(this.configWindow);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open settings. Subcommands: on, off, reload, status, dump, probe, find &lt;text&gt;, clearmisses",
        });

        pluginInterface.UiBuilder.Draw += this.DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;

        this.clientState.Login += this.OnLogin;
    }

    /// <summary>
    ///     Rebuilds the index when a character logs in.
    /// </summary>
    /// <remarks>
    ///     Keys are built by evaluating macros against live game state, so they embed the character's
    ///     name, gender and Grand Company rank. A corpus indexed for one character does not match
    ///     another. Rebuilding costs one load — under a second — and most players only ever trigger it
    ///     once per session.
    /// </remarks>
    private void OnLogin()
    {
        var name = this.playerState.IsLoaded ? this.playerState.CharacterName : "(unknown)";
        this.log.Information("Login as '{Name}'; re-indexing so macro-derived keys match this character.", name);
        this.store.Load(this.TranslationFileCandidates());
    }

    /// <summary>
    ///     Draws the window set, then the file picker.
    /// </summary>
    /// <remarks>
    ///     The picker must be drawn outside any Begin/End pair, so it cannot live inside
    ///     <see cref="ConfigWindow" />.Draw — hence drawing it here, after the window system.
    /// </remarks>
    private void DrawUi()
    {
        this.windows.Draw();
        this.fileDialogs.Draw();
    }

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenConfig;

        this.clientState.Login -= this.OnLogin;
        this.commands.RemoveHandler(CommandName);
        this.fileDialogs.Reset();

        this.talkHandler.Dispose();
        this.overlays.Dispose();
        this.finder.Dispose();
        this.misses.Dispose();
        this.windows.RemoveAllWindows();
    }

    /// <summary>
    ///     Where to look for the translation file.
    /// </summary>
    /// <remarks>
    ///     Nothing is bundled with the plugin: a corpus is hundreds of MB, is a derivative of the
    ///     game's own text, and evolves separately from the code, so it ships neither in the
    ///     repository nor in the distribution zip. An explicitly configured path wins; otherwise the
    ///     plugin config directory is searched for <see cref="TranslationFileNames" />.
    /// </remarks>
    private IEnumerable<string> TranslationFileCandidates()
    {
        if (!string.IsNullOrWhiteSpace(this.config.CorpusPath))
        {
            yield return this.config.CorpusPath.Trim();
        }

        var configDirectory = this.pluginInterface.GetPluginConfigDirectory();
        foreach (var name in TranslationFileNames)
        {
            yield return Path.Combine(configDirectory, name);
        }

        // Last, so any real corpus wins. Loading it is better than loading nothing: with no file at
        // all the plugin is indistinguishable from one that is broken, and the first thing anyone
        // asks is whether it installed correctly. Two lines answer that. The settings window says in
        // red when this is what is loaded, so nobody mistakes it for a translation.
        yield return this.SampleCorpusPath;
    }

    /// <summary>Absolute path of the bundled sample, which sits beside the plugin DLL.</summary>
    private string SampleCorpusPath =>
        Path.Combine(
            this.pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty,
            SampleCorpusRelativePath);

    private void OnCommand(string command, string arguments)
    {
        var trimmed = arguments.Trim();

        // Handled before the switch because its argument is free text whose case matters, and the
        // switch lowercases everything.
        if (trimmed.StartsWith("find", StringComparison.OrdinalIgnoreCase))
        {
            var needle = trimmed[4..].Trim();
            this.finder.Hunt(needle);
            this.config.FindText = needle;
            this.SaveConfig(this.config);
            this.chat.Print(needle.Length > 0
                ? $"[Gubal]Watching every addon for \"{needle}\" — see /xllog. Clear it with /gubal find"
                : "[Gubal]Addon search off.");
            return;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "":
                this.OpenConfig();
                break;

            case "on":
                this.config.Enabled = true;
                this.SaveConfig(this.config);
                this.chat.Print("[Gubal]Enabled.");
                break;

            case "off":
                this.config.Enabled = false;
                this.SaveConfig(this.config);
                this.chat.Print("[Gubal]Disabled.");
                break;

            case "reload":
                this.ReloadTranslations();
                break;

            case "status":
                this.PrintStatus();
                break;

            case "dump":
                this.config.LogMisses = !this.config.LogMisses;
                this.SaveConfig(this.config);
                this.chat.Print($"[Gubal]Miss logging {(this.config.LogMisses ? "on" : "off")}.");
                break;

            case "probe":
                this.config.ProbeEvents = !this.config.ProbeEvents;
                this.SaveConfig(this.config);
                this.chat.Print($"[Gubal]Event probe {(this.config.ProbeEvents ? "on" : "off")} — see /xllog.");
                break;

            case "clearmisses":
                this.misses.Reset();
                this.chat.Print("[Gubal]Miss log cleared.");
                break;

            default:
                this.chat.Print("[Gubal]Usage: /gubal [on|off|reload|status|dump|probe|find <text>|clearmisses]");
                break;
        }
    }

    private void ReloadTranslations()
    {
        if (this.store.Load(this.TranslationFileCandidates()))
        {
            this.chat.Print($"[Gubal]Reloaded {this.store.Count} entries from {this.store.LoadedFrom}");
        }
        else
        {
            this.chat.PrintError("[Gubal]Failed to load a translation file — see /xllog.");
        }
    }

    private void PrintStatus()
    {
        var snapshot = this.Snapshot();
        this.chat.Print($"[Gubal]{snapshot.EntryCount} entries, {snapshot.NpcNameCount} NPC names.");
        this.chat.Print($"[Gubal]Source: {snapshot.LoadedFrom}");
        this.chat.Print($"[Gubal]Injected {snapshot.InjectedCount} line(s), {snapshot.MissCount} distinct miss(es).");
        var character = this.playerState.IsLoaded ? this.playerState.CharacterName : "(not loaded)";
        this.chat.Print($"[Gubal]Enabled={this.config.Enabled}, indexed for '{character}'");
    }

    private StatusSnapshot Snapshot()
    {
        return new StatusSnapshot(
            this.store.Count,
            this.store.NpcNameCount,
            this.talkHandler.InjectedCount,
            this.misses.Count,
            this.store.LoadedFrom,
            this.misses.Path,
            this.pluginInterface.GetPluginConfigDirectory(),
            string.Equals(this.store.LoadedPath, this.SampleCorpusPath, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveConfig(Configuration configuration)
    {
        this.pluginInterface.SavePluginConfig(configuration);
    }

    private void OpenConfig()
    {
        this.configWindow.IsOpen = true;
    }
}
