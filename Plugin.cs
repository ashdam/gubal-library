using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Hands the game pre-translated Excel pages so it draws another language through its own
///     text pipeline.
/// </summary>
/// <remarks>
///     Makes no network calls beyond fetching a pack somebody else built; translation happens offline
///     in a separate pipeline. It used to do the opposite — intercept what the game was about to draw
///     and swap the text in a UI node, some 3,500 lines of it — and the file route replaced that once
///     it was proven to reach the sheets the client reads at boot, which injection never could.
/// </remarks>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gubal";

    /// <summary>Identifies this plugin's one chat link. Scoped to the plugin, so any value will do.</summary>
    private const uint OpenConfigLinkId = 1;

    /// <summary>How long the startup update may hold the game's boot before it is abandoned.</summary>
    /// <remarks>
    ///     A ceiling, not an expectation: what it bounds is a host that accepts the connection and
    ///     then says nothing. Without it the ten-minute download timeout would be how long somebody's
    ///     game sits at a black screen.
    /// </remarks>
    private static readonly TimeSpan BootUpdateBudget = TimeSpan.FromMinutes(2);

    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly FileDialogManager fileDialogs = new();
    private readonly Configuration config;
    private readonly ConfigWindow configWindow;
    private readonly IDalamudPluginInterface pluginInterface;

    private readonly ExdRedirector? redirector;
    private readonly string? redirectorError;
    private readonly PackInstaller installer;

    /// <summary>The installed pack's contents, read once per pack rather than once per frame.</summary>
    private PackContents? contents;

    /// <summary>Which folder <see cref="contents" /> was read from, so a change is noticed.</summary>
    private string contentsPath = string.Empty;

    private readonly SqPackProbe? probe;
    private readonly WindowSystem windows = new("GubalLibrary");

    /// <summary>Makes the words "open the settings" in the chat announcement clickable.</summary>
    private readonly DalamudLinkPayload openConfigLink;

    /// <summary>What the background check made of the pack's declared update address.</summary>
    private UpdateStatus update;

    /// <summary>The version taken during startup, or null when nothing was.</summary>
    /// <remarks>
    ///     Worth a line of chat precisely because there is nothing to do about it: a translation that
    ///     silently improves is indistinguishable from one that silently broke unless it is said.
    /// </remarks>
    private readonly string? bootUpdate;

    /// <summary>Whether that line has been said, so a character change does not repeat it.</summary>
    private bool bootUpdateAnnounced;

    /// <summary>True while a check is in flight, so a second one is not started on top of it.</summary>
    /// <remarks>
    ///     Every caller that sets it runs on the game's own thread; only the worker clears it, from a
    ///     background thread, which is what <c>volatile</c> is for.
    /// </remarks>
    private volatile bool checking;

    /// <summary>The version already said out loud, so it is said once rather than once per login.</summary>
    /// <remarks>
    ///     Keyed on the version rather than a bare flag: switching character is a logout and a login,
    ///     so a per-session flag would either repeat the line every alt or swallow a newer version.
    /// </remarks>
    private volatile string? announcedVersion;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IChatGui chat,
        IClientState clientState,
        IFramework framework,
        IGameInteropProvider interop,
        ITextureProvider textures,
        IPluginLog log)
    {
        // Before anything else reads a string: CheapLoc answers "#Key" rather than the English
        // fallback for an assembly it has not been set up for.
        Language.Apply(pluginInterface.UiLanguage, log);

        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        this.clientState = clientState;
        this.framework = framework;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

        // First, because what it measures is how early this plugin runs: anything queued ahead of it
        // would be measuring itself.
        this.probe = this.config.ProbeSqPack ? new SqPackProbe(interop, log) : null;

        this.installer = new PackInstaller(log, pluginInterface.GetPluginConfigDirectory());

        // Before the redirector, and blocking, which is the point: what is fetched has to be on disk
        // before the pages are enumerated, or it is a generation too late.
        this.bootUpdate = this.UpdateBeforeTheGameReads(log);

        // Installed in the constructor and nowhere else — the whole reason the route works. The
        // client reads its sheets about two seconds after plugins load and keeps them for the
        // session, so a redirection put in place later is invisible for everything already read.
        if (this.config.ServeLanguagePack && this.config.LanguagePackPath.Length > 0)
        {
            (this.redirector, this.redirectorError) = ExdRedirector.Create(
                interop,
                log,
                this.config.LanguagePackPath,
                this.Contents(),
                this.config.DisabledSheets);

            if (this.redirectorError is { Length: > 0 } error)
            {
                log.Warning("Translated pages are not being served: {Error}", error);
            }
        }

        this.configWindow = new ConfigWindow(
            this.config,
            this.SaveConfig,
            this.fileDialogs,
            this.PageSnapshot,
            this.Contents,
            textures,
            this.installer,
            this.OnPackInstalled,
            () => this.BeginUpdateCheck(verbose: false),
            this.SetAutoUpdate,
            () => DalamudBootWait.IsOn(this.pluginInterface),
            () => pluginInterface.OpenDalamudSettingsTo(SettingsOpenKind.General),
            pluginInterface.Manifest.AssemblyVersion.ToString());

        this.windows.AddWindow(this.configWindow);

        this.openConfigLink = chat.AddChatLinkHandler(OpenConfigLinkId, (_, _) => this.OpenConfig());

        // Announced when the check finishes, or at the next login if that is later: loaded at boot the
        // answer arrives at the title screen, where chat goes nowhere.
        this.clientState.Login += this.OnLogin;
        pluginInterface.LanguageChanged += this.OnLanguageChanged;

        this.BeginUpdateCheck(verbose: false);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open settings. See the plugin description for subcommands.",
        });

        pluginInterface.UiBuilder.Draw += this.DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;
    }

    /// <summary>Draws the window set, then the file picker.</summary>
    /// <remarks>
    ///     The picker must be drawn outside any Begin/End pair, so it cannot live inside
    ///     <see cref="ConfigWindow" />.Draw.
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
        this.pluginInterface.LanguageChanged -= this.OnLanguageChanged;
        this.chat.RemoveChatLinkHandler();

        this.commands.RemoveHandler(CommandName);
        this.fileDialogs.Reset();

        this.redirector?.Dispose();
        this.probe?.Dispose();

        this.windows.RemoveAllWindows();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                this.OpenConfig();
                break;

            case "status":
                this.PrintStatus();
                break;

            // Reports only. There is no subcommand to switch a part on or off: the window does that,
            // and a second way to write the same setting is a second way for it to disagree.
            case "parts":
                this.PrintParts();
                break;

            // Verbose, unlike the check at load: somebody who asked is owed an answer even when it is
            // "nothing to do", and a command that prints nothing reads as one that did nothing.
            case "check":
                this.chat.Print(this.BeginUpdateCheck(verbose: true)
                    ? "[Gubal]Asking whether a newer language pack is published..."
                    // Not "the answer follows": the check already running is the quiet one from load.
                    : "[Gubal]A check is already running — /gubal shows what it comes back with.");
                break;

            // Exists so recovering from a bad run needs no text editor: the route detours the function
            // every file goes through, so getting it wrong is a crashed client — and a crashed client
            // cannot be used to turn it off.
            case "usepack":
                this.config.ServeLanguagePack = !this.config.ServeLanguagePack;
                this.SaveConfig(this.config);
                this.chat.Print(this.config.ServeLanguagePack
                    ? "[Gubal]Language pack ON from the next start. The game reads its text once at startup."
                    : "[Gubal]Language pack OFF from the next start.");
                break;

            case "autoupdate":
                this.SetAutoUpdate(!this.config.AutoUpdatePack);

                if (!this.config.AutoUpdatePack)
                {
                    this.chat.Print("[Gubal]Startup updates OFF. Newer packs are offered, not taken.");
                    break;
                }

                // Said even when it worked, because it changes a setting that is not this plugin's and
                // that governs how every plugin loads.
                this.chat.Print(DalamudBootWait.IsOn(this.pluginInterface) is true
                    ? "[Gubal]Startup updates ON. Dalamud will hold the game's start while a newer pack is fetched."
                    : "[Gubal]Startup updates ON, but Dalamud is not waiting for plugins before the game loads "
                      + "and could not be set to. Turn it on in Dalamud's settings or this does nothing.");
                break;

            // Next load, not now: what it measures is how early the plugin attaches.
            case "probesqpack":
                this.config.ProbeSqPack = !this.config.ProbeSqPack;
                this.SaveConfig(this.config);
                this.chat.Print(this.config.ProbeSqPack
                    ? "[Gubal]SqPack probe ON. Restart the client — it attaches at load and only then."
                    : "[Gubal]SqPack probe OFF from the next load.");
                break;

            default:
                this.chat.Print("[Gubal]Usage: /gubal [status|parts|check|usepack|autoupdate|probesqpack]");
                break;
        }
    }

    private void PrintStatus()
    {
        var pages = this.PageSnapshot();

        if (pages is { Active: true, Manifest: { } pack })
        {
            this.chat.Print($"[Gubal]{pack.DisplayName} ({pack.TranslationVersion ?? "unversioned"})");
            this.chat.Print(
                $"[Gubal]{pack.Lines:N0} of {pack.Rows:N0} line(s) translated across {pages.PageCount:N0} page(s), "
                + $"built for game {pack.GameVersion ?? "unknown"}.");

            // The number that separates "loaded" from "working": a route installed too late to matter
            // reports the two lines above identically.
            this.chat.Print($"[Gubal]{pages.ServedCount:N0} read(s) answered from disk this session.");

            // Otherwise somebody reports a bug about text that is English exactly as they asked.
            if (this.Contents().PartsOff(this.config.DisabledSheets) is { Count: > 0 } off)
            {
                this.chat.Print($"[Gubal]Switched off on purpose, so still English: {string.Join(", ", off)}.");
            }

            return;
        }

        this.chat.PrintError(
            $"[Gubal]No pages served — {pages.Error ?? "no language pack configured."}");
    }

    /// <summary>Lists the parts of the translation and whether each is being served.</summary>
    /// <remarks>
    ///     Grouped rather than one checkbox at a time: nineteen lines of chat to answer "is the
    ///     interface translated" would bury the answer in the question.
    /// </remarks>
    private void PrintParts()
    {
        var pack = this.Contents();

        if (pack.Layout.Count == 0)
        {
            this.chat.Print("[Gubal]No language pack is installed, so there are no parts to list.");
            return;
        }

        foreach (var group in pack.Layout)
        {
            var off = group.Parts.Count(p => p.Sheets.All(this.config.DisabledSheets.Contains));

            var state = off switch
            {
                0 => "on",
                _ when off == group.Parts.Length => "off",
                _ => $"{group.Parts.Length - off} of {group.Parts.Length} on",
            };

            this.chat.Print($"[Gubal]{group.Name}: {state}");
        }

        this.chat.Print("[Gubal]Change these under Translated parts in /gubal. They take effect at the next start.");
    }

    /// <summary>What the installed pack holds, read from disk the first time and kept.</summary>
    /// <remarks>
    ///     Cached here rather than in the window because both need it and only one can afford to read
    ///     it: the window calls this every frame and a pack is four thousand files. The path is
    ///     compared each time, so pointing at a different folder is noticed. Read even when nothing is
    ///     served — somebody choosing parts is usually looking at a pack that is off, or waiting for a
    ///     restart.
    /// </remarks>
    private PackContents Contents()
    {
        var path = this.config.LanguagePackPath;

        if (this.contents is null || !string.Equals(this.contentsPath, path, StringComparison.OrdinalIgnoreCase))
        {
            this.contents = PackContents.Load(path, ExdRedirector.MaxLocalPathLength);
            this.contentsPath = path;
        }

        return this.contents;
    }

    /// <summary>What the settings window and <c>/gubal status</c> report about the file route.</summary>
    /// <remarks>
    ///     Both counts, deliberately: pages registered says the folder was read, reads answered says
    ///     the game is taking them, and only the second distinguishes a working route from one
    ///     installed too late to matter.
    /// </remarks>
    private PageStatus PageSnapshot()
    {
        return this.redirector is { } r
            ? new PageStatus(true, r.PageCount, r.ServedCount, null, r.Manifest, this.update)
            : new PageStatus(false, 0, 0, this.redirectorError, null, this.update);
    }

    /// <summary>
    ///     Called when a pack has just been installed, which invalidates what was known about updates.
    /// </summary>
    /// <remarks>
    ///     The check ran against the pack being served, so once a different one is on disk that answer
    ///     describes something on its way out — including, in the case that prompted this, a red "no
    ///     connection" complaint left on screen after a successful install. Cleared rather than
    ///     recomputed: the new pack is not served until the client restarts, so the honest thing to
    ///     report about it is nothing.
    /// </remarks>
    private void OnPackInstalled()
    {
        this.update = default;

        // Dropped rather than reloaded: the folder may be the same one, so nothing else would notice
        // it had changed underneath. The window rebuilds it on its next frame.
        this.contents = null;

        // In chat as well as the window, because the window is where the person just was and chat is
        // where they will be.
        this.chat.Print("[Gubal]Language pack installed. RESTART THE CLIENT — the game reads its text once at startup.");
    }

    /// <summary>
    ///     Asks, in the background, whether the publisher has a newer generation.
    /// </summary>
    /// <remarks>
    ///     Nothing is downloaded and nothing changed: it sets a field the window reads and may print
    ///     one line of chat, because updating means replacing thousands of files and restarting.
    ///     Never awaited — it reaches the network, and anywhere on the constructor's path it would
    ///     spend the startup margin the whole design rests on.
    /// </remarks>
    /// <param name="verbose">Report every outcome in chat, not only a newer version.</param>
    /// <returns>False when one was already running, so nothing new was started.</returns>
    private bool BeginUpdateCheck(bool verbose)
    {
        if (this.checking)
        {
            return false;
        }

        this.checking = true;

        // Back to Checking, the only honest thing to say while the question is open: otherwise the
        // window keeps asserting the previous answer under a button just pressed to replace it.
        this.update = default;

        _ = Task.Run(() => this.CheckForUpdateAsync(verbose));
        return true;
    }

    /// <summary>
    ///     Takes a newer pack during startup, while the game is still waiting for its plugins.
    /// </summary>
    /// <remarks>
    ///     <b>Blocking, deliberately, and the only thing here that is.</b> Finish before the client's
    ///     first read and the new translation is live this session rather than the next. It works only
    ///     because Dalamud can be asked to hold the boot, which is the player's setting: with it off
    ///     the client reads mid-download and the session loses its translation altogether, so the
    ///     answer is checked rather than hoped for and "cannot tell" counts as no. The other
    ///     preconditions each prevent damage done unasked — a pack the user pointed at is their folder
    ///     to manage, and a non-URL source would re-unpack the same bytes on every boot forever.
    /// </remarks>
    /// <returns>The version now installed, or null when nothing was taken.</returns>
    private string? UpdateBeforeTheGameReads(IPluginLog log)
    {
        if (!this.config.AutoUpdatePack
            || !this.config.ServeLanguagePack
            || !PackInstaller.IsRemote(this.config.PackSource)
            || !string.Equals(
                this.config.LanguagePackPath, this.installer.InstalledPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (DalamudBootWait.IsOn(this.pluginInterface) is not true)
        {
            log.Warning(
                "Not updating the language pack during startup: Dalamud is not waiting for plugins "
                + "before the game loads, so the client would read its text while the download was "
                + "still running. Turn that on in Dalamud's settings, or update from the window.");
            return null;
        }

        try
        {
            using var budget = new CancellationTokenSource(BootUpdateBudget);
            return Task.Run(() => this.TakeUpdateAsync(log, budget.Token)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Nothing below is expected to throw. This is here because the one thing that must not
            // happen at this point is a plugin that fails to construct and takes the language with it.
            log.Error(e, "Updating the language pack during startup failed.");
            return null;
        }
    }

    private async Task<string?> TakeUpdateAsync(IPluginLog log, CancellationToken cancel)
    {
        var installed = PackManifest.Read(this.config.LanguagePackPath).Manifest;

        if (await this.installer.CheckForUpdateAsync(installed, cancel).ConfigureAwait(false)
            is not { State: UpdateState.Available, Published: { } published })
        {
            return null;
        }

        // Refused rather than taken: a pack built for a patch this client is not running would be
        // turned away by the redirector a moment from now, trading a translation that works for none.
        // It happens for an ordinary reason — a publisher preparing the next patch's pack early.
        var version = published.TranslationVersion ?? "unversioned";
        var running = ExdRedirector.RunningGameVersion();

        if (published.GameVersion is { Length: > 0 } builtFor && builtFor != running)
        {
            log.Warning(
                "Not taking language pack {Version}: it is built for game {BuiltFor} and this client "
                + "runs {Running}. The installed pack is left alone.",
                version,
                builtFor,
                running ?? "an unknown version");
            return null;
        }

        log.Information("Taking language pack {Version} before the game reads its text.", version);

        var result = await this.installer.InstallAsync(this.config.PackSource, null, cancel).ConfigureAwait(false);

        if (!result.Success)
        {
            log.Warning(
                "The startup update did not happen: {Error} The installed pack is untouched.",
                result.Error ?? "no reason given.");
            return null;
        }

        return result.Manifest?.TranslationVersion ?? "unversioned";
    }

    /// <summary>
    ///     Turns the startup update on or off, and Dalamud's boot wait along with it.
    /// </summary>
    /// <remarks>
    ///     One decision from where the user sits — a box that says the game will wait and then does
    ///     not wait is a broken box. <b>One direction only:</b> unticking leaves Dalamud alone,
    ///     because the setting is global, governs how every plugin loads, and may have been wanted
    ///     long before this box existed.
    /// </remarks>
    private void SetAutoUpdate(bool value)
    {
        this.config.AutoUpdatePack = value;

        if (value && DalamudBootWait.IsOn(this.pluginInterface) is not true)
        {
            DalamudBootWait.TryTurnOn();
        }

        this.SaveConfig(this.config);
    }

    private async Task CheckForUpdateAsync(bool verbose)
    {
        try
        {
            // From the pack rather than from the configuration: the address travels inside whatever
            // is installed, so a publisher who moves hosts takes their existing users with them.
            // Caching it here would undo exactly that.
            var installed = this.redirector?.Manifest ?? this.InstalledManifest();

            this.update = await this.installer.CheckForUpdateAsync(installed).ConfigureAwait(false);
        }
        finally
        {
            this.checking = false;
        }

        this.Announce(verbose);
    }

    /// <summary>
    ///     Says in chat what the check found, if there is anybody there to read it.
    /// </summary>
    /// <remarks>
    ///     <b>Only a newer version is announced unprompted.</b> An address that does not answer, or a
    ///     pack that declares none, are said in the window and the log but are not worth a red line to
    ///     somebody who has just sat down to play. Called from two places because the answer can
    ///     arrive before the player does; <see cref="announcedVersion" /> keeps it from printing twice.
    /// </remarks>
    private void Announce(bool verbose)
    {
        var status = this.update;

        if (status is { State: UpdateState.Available, Published: { TranslationVersion: { Length: > 0 } version } published })
        {
            // Nothing at the title screen: chat does not exist there. The Login handler comes back to
            // this the moment it does.
            if (!verbose && (version == this.announcedVersion || !this.clientState.IsLoggedIn))
            {
                return;
            }

            this.announcedVersion = version;
            this.Print(() => this.chat.Print(BuildUpdateAnnouncement(published, this.openConfigLink)));
            return;
        }

        if (!verbose)
        {
            return;
        }

        // The installer answers NotDeclared for both "no pack" and "a pack that names no address",
        // which are one silence from its side and two sentences from the user's.
        var line = status.State switch
        {
            UpdateState.UpToDate => "[Gubal]The installed language pack is the latest one published.",
            UpdateState.NotDeclared when (this.redirector?.Manifest ?? this.InstalledManifest()) is null =>
                "[Gubal]No language pack is installed, so there is nothing to check.",
            UpdateState.NotDeclared =>
                "[Gubal]This language pack declares no update address, so it cannot say whether a newer one exists.",
            UpdateState.Unreachable => "[Gubal]Could not reach the language pack's update address. See /xllog for why.",
            _ => null,
        };

        if (line is not null)
        {
            this.Print(() => this.chat.Print(line));
        }
    }

    /// <summary>Redraws the window in the language Dalamud has just been switched to.</summary>
    /// <remarks>
    ///     The two caches have to go with it: the parts table is built out of localized strings, and
    ///     the layout the window draws is built out of the table. Nothing else is language-dependent
    ///     — a sheet key is the game's and does not move.
    /// </remarks>
    private void OnLanguageChanged(string code)
    {
        Language.Apply(code, this.log);
        PackParts.Invalidate();
        this.contents = null;
    }

    /// <summary>Announces whatever the check found, now that there is a chat window to print into.</summary>
    private void OnLogin()
    {
        if (this.bootUpdate is { Length: > 0 } version && !this.bootUpdateAnnounced)
        {
            this.bootUpdateAnnounced = true;
            this.chat.Print($"[Gubal]Language pack updated to {version} before this session started.");
        }

        this.Announce(verbose: false);
    }

    /// <summary>One line of chat, built here and printed where the game expects to be spoken to.</summary>
    /// <remarks>
    ///     The clickable part is what somebody will try to click anyway. Both the link and the command
    ///     are in there, since a chat log scrolled past a link still has to be actionable.
    /// </remarks>
    private static SeString BuildUpdateAnnouncement(PackManifest published, DalamudLinkPayload link)
    {
        var game = published.GameVersion is { Length: > 0 } built ? $", built for game {built}" : string.Empty;

        return new SeStringBuilder()
            .AddText($"[Gubal]A newer {published.DisplayName} is published: {published.TranslationVersion}{game}. ")
            .Add(link)
            .AddUiForeground(539)
            .AddText("Open the settings")
            .AddUiForegroundOff()
            .Add(RawPayload.LinkTerminator)
            .AddText(" or type /gubal to install it. The client has to restart afterwards.")
            .Build();
    }

    /// <summary>Prints from the game's own thread, wherever the caller happens to be.</summary>
    private void Print(Action print)
    {
        // A check outlives the plugin whenever somebody reloads it while one is in flight, which on a
        // dev build is most of them. There is nobody left to tell, and the queue is being torn down.
        if (this.framework.IsFrameworkUnloading)
        {
            return;
        }

        if (this.framework.IsInFrameworkUpdateThread)
        {
            print();
            return;
        }

        _ = this.framework.RunOnFrameworkThread(print);
    }

    /// <summary>The manifest of the configured pack when it is not being served.</summary>
    /// <remarks>
    ///     Someone who has turned the pack off, or installed one and not restarted, should still be
    ///     told a newer one exists: those are the states where they are most likely to act on it.
    /// </remarks>
    private PackManifest? InstalledManifest()
    {
        return this.config.LanguagePackPath.Length > 0
            ? PackManifest.Read(this.config.LanguagePackPath).Manifest
            : null;
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
