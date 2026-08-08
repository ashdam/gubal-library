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
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly MissLog misses;
    private readonly IPlayerState playerState;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly TranslationStore store;
    private readonly MacroResolver resolver;
    private readonly TalkHandler talkHandler;
    private readonly OverlayHandler overlays;
    private readonly AddonFinder finder;

    private readonly ExdRedirector? redirector;
    private readonly string? redirectorError;

    private readonly SqPackProbe? probe;
    private readonly WindowSystem windows = new("GubalLibrary");

    /// <summary>Cancels a rebuild that has been queued but not yet run when the plugin unloads.</summary>
    /// <remarks>
    ///     Indexing the corpus takes seconds and touches nothing but managed memory, so a stray run
    ///     after unload would not be dangerous — only wasteful, and it would log as though the plugin
    ///     were still live. Cheap enough to just not do.
    /// </remarks>
    private readonly CancellationTokenSource unloading = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IPlayerState playerState,
        IClientState clientState,
        IAddonLifecycle addonLifecycle,
        IChatGui chat,
        IFramework framework,
        ISeStringEvaluator evaluator,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.playerState = playerState;
        this.clientState = clientState;
        this.chat = chat;
        this.framework = framework;
        this.log = log;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

        this.store = new TranslationStore(log, evaluator);

        // Not loaded unconditionally, and never loaded from here directly. Two separate rules.
        //
        // IsLoggedIn, because keys are built by resolving macros against live game state — the
        // character's name, gender and Grand Company rank — so building before a character exists
        // produces keys that cannot match, and once crashed outright.
        //
        // On the next tick, because a plugin constructor does not run on the game's update thread and
        // the evaluator needs to be on it to read that state. Enabling the plugin mid-session — the
        // first thing anyone does after installing it — therefore built a quietly broken index:
        // every line with an <if( or <switch( threw and was dropped, and every player-name line was
        // keyed under a string with a hole where the name goes. Measured on one session, minutes
        // apart: 71,495 entries and 903 dropped through here, against 72,337 and none through
        // /gubal reload. Nothing on screen distinguishes that from an untranslated line.
        //
        // The deferral fixes the cause; LoadCorpus's canary catches the symptom whatever the cause.
        if (clientState.IsLoggedIn)
        {
            this.RebuildOnNextTick("the plugin was enabled with a session already running");
        }
        else
        {
            log.Information("No character logged in yet; the corpus loads once one is.");
        }

        this.misses = new MissLog(
            log,
            Path.Combine(pluginInterface.GetPluginConfigDirectory(), MissLogFileName));

        // One resolver for both handlers, so the "will not evaluate, not injected" rule and its
        // failure count are stated once rather than reimplemented per addon.
        this.resolver = new MacroResolver(evaluator, log);

        this.talkHandler = new TalkHandler(
            addonLifecycle,
            log,
            this.config,
            this.store,
            this.misses,
            this.resolver,
            clientState);

        // One handler, several candidate addon names. TalkSubtitle is proven; the others are guesses
        // that cost nothing if they never fire and announce themselves in the log if they do.
        //
        // JournalDetail is not a guess: /gubal find traced the Duty Finder's description to it, and
        // it is the panel the game shows for a selected duty. It is deliberately handled through its
        // values only, which OverlayHandler.ValueOnly explains.
        this.overlays = new OverlayHandler(
            addonLifecycle,
            log,
            this.config,
            this.store,
            this.misses,
            this.resolver,
            clientState,
            "TalkSubtitle",
            "_BattleTalk",
            "_ScreenInfoFront",
            "_MiniTalk",
            "MiniTalk",
            "JournalDetail",

            // The quest list down the left of the journal, which draws the same titles JournalDetail
            // does and drew them in English while the detail panel beside it was Spanish. Not a guess:
            // /gubal find traced "The Price of Principles" to it, node 3. See BodyNodes.
            "Journal",

            // The quest tracker under the minimap: the same title again, plus the current objective.
            // Also traced rather than guessed — nodes 2 and 6. It is the one addon here that is on
            // screen permanently, which is why BodyNodes must name its nodes and not sweep them.
            "_ToDoList",

            // The dialogue choice list, and the menu that precedes it. SelectString is not a guess:
            // /gubal find traced Urianger's "What of the primals?" to it, node 2. See BodyNode.
            //
            // SelectIconString is the icon list — the first menu an NPC with several services shows,
            // "Small Talk" over the quests they offer. It is a guess, made on this file's standing
            // terms: it never fired on Urianger, who has nothing but small talk and so opens the
            // choice list directly, and there was no second NPC to hand. Deliberately absent from
            // BodyNode, so an addon nobody has characterised gets the full node sweep and the miss log
            // reveals its layout — the progression that established both ids above.
            "SelectString",
            "SelectIconString");

        this.finder = new AddonFinder(addonLifecycle, log);
        this.finder.Hunt(this.config.FindText);

        // Attached first thing, before anything else in the constructor, because what it is measuring
        // is how early this plugin runs. Anything queued ahead of it would be measuring itself.
        this.probe = this.config.ProbeSqPack ? new SqPackProbe(interop, log) : null;

        // The second route to the same translations: hand the game rebuilt Excel pages instead of
        // swapping text in a UI node. It coexists with injection rather than replacing it — a page
        // the corpus has not covered simply stays English and the handlers above still get their
        // chance at it.
        //
        // Installed here, in the constructor, and nowhere else. This is the whole reason the route
        // works: the client reads its sheets about two seconds after plugins load and keeps them for
        // the session, so a redirection put in place any later is invisible for everything already
        // read. Measured, and it is why the guildhest descriptions stayed English for a session.
        if (this.config.ServePages && this.config.PagesPath.Length > 0)
        {
            (this.redirector, this.redirectorError) =
                ExdRedirector.Create(interop, log, this.config.PagesPath);

            if (this.redirectorError is { Length: > 0 } error)
            {
                log.Warning("Translated pages are not being served: {Error}", error);
            }
        }

        this.configWindow = new ConfigWindow(
            this.config,
            this.SaveConfig,
            this.Snapshot,
            this.fileDialogs,
            this.PageSnapshot,
            pluginInterface.Manifest.AssemblyVersion.ToString())
        {
            OnReloadRequested = this.ReloadTranslations,
        };
        this.windows.AddWindow(this.configWindow);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open settings. Subcommands: on, off, servepages, reload, status, dump, "
                + "probe, find &lt;text&gt;, clearmisses",
        });

        pluginInterface.UiBuilder.Draw += this.DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;

        this.clientState.Login += this.OnLogin;
    }

    /// <summary>
    ///     Builds the index when a character logs in, and rebuilds it when a different one does.
    /// </summary>
    /// <remarks>
    ///     Keys are built by evaluating macros against live game state, so they embed the character's
    ///     name, gender and Grand Company rank. A corpus indexed for one character does not match
    ///     another. For a plugin started at the title screen this is the <em>first</em> build rather
    ///     than a rebuild — the constructor deliberately leaves it undone until there is a character
    ///     to build against. Under a second either way.
    /// </remarks>
    private void OnLogin()
    {
        var name = this.playerState.IsLoaded ? this.playerState.CharacterName : "(unknown)";

        // Inline, not deferred: this event is raised from the game's update thread, so the evaluator
        // can already read state here. That is why this route never produced a degraded index while
        // the constructor's did.
        this.LoadCorpus($"login as '{name}'; keys must match this character");
    }

    /// <summary>
    ///     Builds the index, then checks it against live game state and rebuilds once if it is bad.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The check is <see cref="TranslationStore.IndexIsSound" /> — two canary macros — rather
    ///         than a test for the condition that caused the defect. A degraded index is silently
    ///         partial and looks exactly like a corpus that is missing those lines, so something has
    ///         to assert the difference; asserting it by re-testing the known cause would only catch
    ///         the cause we already fixed.
    ///     </para>
    ///     <para>
    ///         One rebuild, then an error. Retrying forever would turn a corpus defect into a load
    ///         loop, and the second failure is worth a person's attention rather than another frame's.
    ///     </para>
    /// </remarks>
    /// <returns>Whether a translation file was read at all — the canary is reported, not returned.</returns>
    private bool LoadCorpus(string why, bool isRebuild = false)
    {
        this.log.Information("Indexing the corpus: {Why}.", why);

        if (!this.store.Load(this.TranslationFileCandidates()))
        {
            return false;
        }

        // The index the overlays have been failing against is gone, so their record of what is not in
        // it is worthless — and worse than worthless, because it stops them ever asking again. The
        // quest tracker is the addon that proved it: on screen before the first index was built, it
        // missed both its lines against an empty store and stayed English for the session.
        this.overlays.ForgetFailedLookups();

        if (this.store.IndexIsSound(out var complaint))
        {
            return true;
        }

        if (isRebuild)
        {
            // Error, not a warning. A warning here would say the same thing the first one said and be
            // read the same way it was: as noise from a plugin that is working. It is not working —
            // the index in memory is the degraded one, and only /gubal reload replaces it.
            this.log.Error(
                "The index is STILL degraded after rebuilding on the game's update thread: {Complaint}. "
                + "{Dropped} line(s) dropped. Those lines will not be translated this session; "
                + "/gubal reload rebuilds.",
                complaint,
                this.store.DroppedCount);
            return true;
        }

        this.log.Warning(
            "Built an index the evaluator could not resolve game state for: {Complaint}. "
            + "{Dropped} line(s) dropped; rebuilding on the next frame.",
            complaint,
            this.store.DroppedCount);

        this.RebuildOnNextTick("the first index came out degraded", isRebuild: true);
        return true;
    }

    /// <summary>Queues an index build for the game's next update.</summary>
    /// <remarks>
    ///     <c>RunOnTick</c> with a tick of delay rather than <c>RunOnFrameworkThread</c>, which runs
    ///     inline when it is already on that thread. The retry path always is — that is the point of
    ///     the retry — so it would recurse instead of trying again a frame later.
    /// </remarks>
    private void RebuildOnNextTick(string why, bool isRebuild = false)
    {
        _ = this.framework.RunOnTick(
            () => { this.LoadCorpus(why, isRebuild); },
            delayTicks: 1,
            cancellationToken: this.unloading.Token);
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
        this.unloading.Cancel();
        this.unloading.Dispose();

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

        this.redirector?.Dispose();
        this.probe?.Dispose();

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

            // Named for what it governs, not for the plugin, because it no longer governs the plugin.
            // There are two routes to Spanish now and this switch is only one of them: pages being
            // served as files keep being served whatever this says. Reading "Disabled" and then
            // seeing Spanish is a confusing half-second, and the fix is to stop overclaiming.
            case "on":
                this.config.Enabled = true;
                this.SaveConfig(this.config);
                this.chat.Print("[Gubal]Text injection on.");
                break;

            case "off":
                this.config.Enabled = false;
                this.SaveConfig(this.config);
                this.chat.Print(this.redirector is not null
                    ? "[Gubal]Text injection off. Translated pages are still being served as files."
                    : "[Gubal]Text injection off.");
                break;

            // Exists so that recovering from a bad run does not need a text editor. The route
            // installs a detour on the function every file in the game goes through, so getting it
            // wrong is a crashed client — and a crashed client cannot be used to turn it off.
            case "servepages":
                this.config.ServePages = !this.config.ServePages;
                this.SaveConfig(this.config);
                this.chat.Print(this.config.ServePages
                    ? "[Gubal]Translated pages ON from the next start. Sheets are read once at startup."
                    : "[Gubal]Translated pages OFF from the next start.");
                break;

            // Takes effect on the next load, not now, and that is the whole point: what it measures
            // is how early the plugin attaches, so attaching it mid-session would measure nothing.
            case "probesqpack":
                this.config.ProbeSqPack = !this.config.ProbeSqPack;
                this.SaveConfig(this.config);
                this.chat.Print(this.config.ProbeSqPack
                    ? "[Gubal]SqPack probe ON. Restart the client — it attaches at load and only then."
                    : "[Gubal]SqPack probe OFF from the next load.");
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
                this.chat.Print(
                    "[Gubal]Usage: /gubal [on|off|servepages|reload|status|dump|probe|find <text>|clearmisses]");
                break;
        }
    }

    private void ReloadTranslations()
    {
        if (this.LoadCorpus("/gubal reload"))
        {
            this.chat.Print(
                $"[Gubal]Reloaded {this.store.Count} entries from {this.store.LoadedFrom}"
                + $" ({this.store.TranslationVersion ?? "no translationVersion — corpus predates the stamp"})");
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
        // The generation, not just the filename. A reload that silently kept the old corpus and one
        // that picked up a freshly merged file print the identical "Source:" line, so the name alone
        // cannot answer the question anybody actually asks after regenerating: is this the new one?
        this.chat.Print(
            $"[Gubal]Source: {snapshot.LoadedFrom}"
            + $" ({snapshot.TranslationVersion ?? "no translationVersion — corpus predates the stamp"})");
        // Split, not summed. Which handler is working is the question this line gets asked, and a
        // total cannot answer it — reporting TalkHandler's count alone once cost a verification round,
        // because "12 injected" was read as proof the subtitle overlay had run when it says nothing
        // about it at all.
        this.chat.Print(
            $"[Gubal]Injected {snapshot.InjectedCount} dialogue line(s) and "
            + $"{snapshot.OverlayInjectedCount} overlay line(s), {snapshot.MissCount} distinct miss(es).");
        if (this.resolver.FailureCount > 0)
        {
            this.chat.Print(
                $"[Gubal]{this.resolver.FailureCount} translation(s) refused: macros would not evaluate. See /xllog.");
        }
        var pages = this.PageSnapshot();
        this.chat.Print(pages switch
        {
            { Active: true } => $"[Gubal]Pages: {pages.PageCount:N0} served as files, "
                + $"{pages.ServedCount:N0} read(s) answered.",
            { Error: { Length: > 0 } error } => $"[Gubal]Pages: not served — {error}",
            _ => "[Gubal]Pages: not served.",
        });

        var character = this.playerState.IsLoaded ? this.playerState.CharacterName : "(not loaded)";
        this.chat.Print($"[Gubal]Enabled={this.config.Enabled}, indexed for '{character}'");
    }

    /// <summary>
    ///     What the settings window and <c>/gubal status</c> report about the file route.
    /// </summary>
    /// <remarks>
    ///     Both numbers, deliberately. How many pages are registered says the folder was read; how
    ///     many reads were answered says the game is actually taking them, and only the second one
    ///     distinguishes a working route from a route that was installed too late to matter.
    /// </remarks>
    private PageStatus PageSnapshot()
    {
        return this.redirector is { } r
            ? new PageStatus(true, r.PageCount, r.ServedCount, null, r.Manifest)
            : new PageStatus(false, 0, 0, this.redirectorError, null);
    }

    private StatusSnapshot Snapshot()
    {
        return new StatusSnapshot(
            this.store.Count,
            this.store.NpcNameCount,
            this.talkHandler.InjectedCount,
            this.overlays.InjectedCount,
            this.misses.Count,
            this.store.LoadedFrom,
            this.store.TranslationVersion,
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
