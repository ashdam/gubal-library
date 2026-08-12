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
///     <para>
///         Makes no network calls. Translation happens entirely offline in a separate pipeline;
///         this plugin only points the game's own file reads at pages somebody else built.
///     </para>
///     <para>
///         It used to do the opposite — intercept what the game was about to draw and swap the text
///         in a UI node — and about 3 500 lines of this plugin were that. The file route replaced it
///         once it was proven to reach the sheets the client reads at boot, which injection never
///         could: it covers every surface rather than the ones somebody wrote a hook for, costs
///         nothing per frame, and lets the engine do its own layout. What is left is small on
///         purpose.
///     </para>
/// </remarks>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gubal";

    /// <summary>Identifies this plugin's one chat link. Scoped to the plugin, so any value will do.</summary>
    private const uint OpenConfigLinkId = 1;

    /// <summary>
    ///     How long the whole startup update may hold the game's boot before it is abandoned.
    /// </summary>
    /// <remarks>
    ///     A ceiling, not an expectation: twenty megabytes takes seconds on anything modern, and what
    ///     this bounds is the case where the host accepts the connection and then says nothing.
    ///     Without it, the plugin's own ten-minute download timeout would be how long somebody's game
    ///     sits at a black screen, and they would have no way to tell that from a hang.
    /// </remarks>
    private static readonly TimeSpan BootUpdateBudget = TimeSpan.FromMinutes(2);

    private readonly IChatGui chat;
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
    ///     Worth a line of chat precisely because there is nothing to do about it: the pack changed
    ///     under somebody between two sessions, and a translation that silently improves is
    ///     indistinguishable from one that silently broke unless it is said.
    /// </remarks>
    private readonly string? bootUpdate;

    /// <summary>Whether that line has been said, so a character change does not repeat it.</summary>
    private bool bootUpdateAnnounced;

    /// <summary>True while a check is in flight, so a second one is not started on top of it.</summary>
    /// <remarks>
    ///     Every caller that sets it — the constructor, the button, the command — runs on the game's
    ///     own thread, so the read and the set cannot interleave with each other. Only the worker
    ///     clears it, from a background thread, which is what <c>volatile</c> is here for.
    /// </remarks>
    private volatile bool checking;

    /// <summary>
    ///     The version already said out loud, so it is said once rather than once per login.
    /// </summary>
    /// <remarks>
    ///     Keyed on the version rather than on a bare "announced" flag, because switching character is
    ///     a logout and a login: a flag cleared per session would repeat the same line every time
    ///     somebody changed alt, and one kept per session would swallow a newer version found later.
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
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.chat = chat;
        this.clientState = clientState;
        this.framework = framework;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

        // Attached first, before anything else, because what it measures is how early this plugin
        // runs. Anything queued ahead of it would be measuring itself.
        this.probe = this.config.ProbeSqPack ? new SqPackProbe(interop, log) : null;

        this.installer = new PackInstaller(log, pluginInterface.GetPluginConfigDirectory());

        // Before the redirector, and blocking, which is the entire point of it being here: what is
        // fetched has to be on disk before the pages are enumerated, or it is a generation too late.
        this.bootUpdate = this.UpdateBeforeTheGameReads(log);

        // Installed here, in the constructor, and nowhere else. This is the whole reason the route
        // works: the client reads its sheets about two seconds after plugins load and keeps them for
        // the session, so a redirection put in place any later is invisible for everything already
        // read. Measured, and it is why the guildhest descriptions once stayed English for a session.
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

        // Announced when the check finishes, or at the next login if that is later. The two orderings
        // are both ordinary: loaded at boot the answer arrives at the title screen, where chat goes
        // nowhere, and hot-reloaded mid-session it arrives with somebody already in the world.
        this.clientState.Login += this.OnLogin;

        this.BeginUpdateCheck(verbose: false);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open settings. Subcommands: status, parts, check, usepack, autoupdate, probesqpack",
        });

        pluginInterface.UiBuilder.Draw += this.DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;
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

            // Reports, and does not change anything. There is no subcommand to switch a part on or
            // off: the window already does that, and a second way to write the same setting is a
            // second way for it to disagree with itself.
            case "parts":
                this.PrintParts();
                break;

            // Verbose, unlike the check at load: somebody who asked is owed an answer even when the
            // answer is "nothing to do", and a command that prints nothing reads as a command that
            // did nothing.
            case "check":
                // The one at load can still be running when somebody types this, and that check is
                // quiet — so saying nothing here would leave the command looking ignored.
                this.chat.Print(this.BeginUpdateCheck(verbose: true)
                    ? "[Gubal]Asking whether a newer language pack is published..."
                    // Deliberately not "the answer follows": the check already running is the quiet
                    // one from load, which says nothing unless it finds something.
                    : "[Gubal]A check is already running — /gubal shows what it comes back with.");
                break;

            // Exists so that recovering from a bad run does not need a text editor. The route
            // installs a detour on the function every file in the game goes through, so getting it
            // wrong is a crashed client — and a crashed client cannot be used to turn it off.
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

                // Said even when it worked, because it is a change to a setting that is not this
                // plugin's and that governs how every plugin loads.
                this.chat.Print(DalamudBootWait.IsOn(this.pluginInterface) is true
                    ? "[Gubal]Startup updates ON. Dalamud will hold the game's start while a newer pack is fetched."
                    : "[Gubal]Startup updates ON, but Dalamud is not waiting for plugins before the game loads "
                      + "and could not be set to. Turn it on in Dalamud's settings or this does nothing.");
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

            // The number that separates "loaded" from "working", and the reason it is printed at all:
            // a route installed too late to matter reports the two lines above identically.
            this.chat.Print($"[Gubal]{pages.ServedCount:N0} read(s) answered from disk this session.");

            // Said here because the alternative is somebody reporting a bug about text that is
            // English exactly as they asked for. A count of served pages cannot tell those apart.
            if (this.Contents().PartsOff(this.config.DisabledSheets) is { Count: > 0 } off)
            {
                this.chat.Print($"[Gubal]Switched off on purpose, so still English: {string.Join(", ", off)}.");
            }

            return;
        }

        this.chat.PrintError(
            $"[Gubal]No pages served — {pages.Error ?? "no language pack configured."}");
    }

    /// <summary>
    ///     Lists the parts of the translation and whether each is being served.
    /// </summary>
    /// <remarks>
    ///     Grouped rather than listed one checkbox at a time: nineteen lines of chat to answer "is the
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

    /// <summary>
    ///     What the settings window and <c>/gubal status</c> report about the file route.
    /// </summary>
    /// <remarks>
    ///     Both counts, deliberately. How many pages are registered says the folder was read; how
    ///     many reads were answered says the game is actually taking them, and only the second one
    ///     distinguishes a working route from a route that was installed too late to matter.
    /// </remarks>
    /// <summary>
    ///     What the installed pack holds, read from disk the first time and kept.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Cached here rather than in the window because both need it and only one of them can
    ///         afford to read it: the settings window calls this every frame, and a pack is four
    ///         thousand files. The path is compared each time so that pointing at a different folder
    ///         is noticed without anybody having to remember to say so.
    ///     </para>
    ///     <para>
    ///         Read even when nothing is being served. Somebody deciding which parts to switch on is
    ///         most often looking at a pack that is switched off, or at one installed a minute ago and
    ///         waiting for a restart, and both of those have to list their contents.
    ///     </para>
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
    ///     <para>
    ///         The update check runs once, at load, against the pack that was being served. Once a
    ///         different pack is on disk that answer describes something that is on its way out —
    ///         including, in the case that prompted this, a red "no connection" complaint about the
    ///         previous pack's update address, still on screen after a successful install from a
    ///         perfectly good one.
    ///     </para>
    ///     <para>
    ///         Cleared rather than recomputed. The newly installed pack is not being served yet and
    ///         will not be until the client restarts, so the honest thing to report about it is
    ///         nothing at all; the check runs again on the next load, against what is actually live.
    ///     </para>
    /// </remarks>
    private void OnPackInstalled()
    {
        this.update = default;

        // Dropped rather than reloaded: the new pack is on disk but the folder may be the same one,
        // so nothing else would notice it had changed underneath. The window rebuilds it on its next
        // frame, which is where the cost belongs.
        this.contents = null;

        // In chat as well as in the window, because the window is where the person just was and chat
        // is where they will be. The instruction is worthless if it is only visible in the place they
        // are about to close.
        this.chat.Print("[Gubal]Language pack installed. RESTART THE CLIENT — the game reads its text once at startup.");
    }

    /// <summary>
    ///     Asks, in the background, whether the publisher has a newer generation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Nothing is downloaded and nothing is changed; it sets a field the window reads and may
    ///         print one line of chat. Updating a pack means replacing thousands of files and then
    ///         restarting the client, which is not something to do to somebody who was about to play —
    ///         so this offers, and the person decides.
    ///     </para>
    ///     <para>
    ///         Never awaited by its callers, and that is the point. It reaches the network, so putting
    ///         it anywhere on the path the constructor takes would spend the startup margin the whole
    ///         design rests on — for a two-kilobyte answer nobody is waiting for.
    ///     </para>
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

        // Back to Checking, which is the enum's default and the only honest thing to say while the
        // question is open. Without it the window would keep asserting the previous answer under a
        // button the user just pressed to replace it.
        this.update = default;

        _ = Task.Run(() => this.CheckForUpdateAsync(verbose));
        return true;
    }

    /// <summary>
    ///     Takes a newer pack during startup, while the game is still waiting for its plugins.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Blocking, deliberately, and the only thing in this plugin that is.</b> Everywhere
    ///         else an install is offered and the user restarts afterwards, because the client reads
    ///         its text once, seconds into startup, and keeps it. Here that ordering is the feature:
    ///         finish before the read and the new translation is live in this session rather than the
    ///         next, and nobody has to be told to restart anything.
    ///     </para>
    ///     <para>
    ///         <b>It only works because Dalamud can be asked to hold the boot for its plugins</b>, and
    ///         that is the player's setting, not this plugin's. With it off the client reads while the
    ///         download is still running and the session loses its translation altogether — strictly
    ///         worse than the restart this is trying to save — so the answer has to be checked rather
    ///         than hoped for, and "cannot tell" counts as no.
    ///     </para>
    ///     <para>
    ///         Every other precondition is local and cheap, and each of them is a way this could
    ///         otherwise do damage unasked: a pack the user pointed at rather than one installed here
    ///         is their folder to manage, and a source that is not a URL would re-unpack the same
    ///         bytes on every boot for the rest of time, since the version on disk would never move.
    ///     </para>
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
            // Nothing below is expected to throw — an abandoned install reports itself as a failure
            // rather than an exception. This is here because the one thing that must not happen at
            // this point is a plugin that fails to construct and takes the whole language with it.
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

        // Refused rather than taken. A pack built for a patch this client is not running will be
        // turned away by the redirector a moment from now, so installing it would trade a
        // translation that works for none at all — and it happens for a perfectly ordinary reason,
        // a publisher preparing the next patch's pack before the player has taken the patch.
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
    ///     <para>
    ///         The two are one decision from where the user sits — a box that says the game will wait
    ///         and then does not wait is a broken box — so ticking this sets the Dalamud side too,
    ///         rather than describing it and leaving somebody to find it in another window.
    ///     </para>
    ///     <para>
    ///         <b>One direction only.</b> Unticking leaves Dalamud exactly as it stands, because by
    ///         then there is no way to know what it means to whoever is looking at it: the setting is
    ///         global, it governs how every plugin loads, and it may have been wanted for its own
    ///         sake long before this box existed. Turning it off is the user's to do, in the window
    ///         that owns it.
    ///     </para>
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
            // Read from the pack rather than from this plugin's configuration, deliberately. The
            // address to poll travels inside whatever is installed, so installing a newer pack
            // replaces it — and a publisher who moves hosts takes their existing users with them.
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
    ///     <para>
    ///         <b>Only a newer version is announced unprompted.</b> An address that does not answer,
    ///         or a pack that declares none, are worth knowing and are said in the settings window and
    ///         in the log — but they are not worth a red line in the chat of somebody who has just sat
    ///         down to play, and neither is anything they can act on there.
    ///     </para>
    ///     <para>
    ///         Called from two places for the same reason a check has two possible orderings: the
    ///         answer can arrive before the player does. Whichever happens second finds the other
    ///         already done, and <see cref="announcedVersion" /> keeps that from printing twice.
    ///     </para>
    /// </remarks>
    private void Announce(bool verbose)
    {
        var status = this.update;

        if (status is { State: UpdateState.Available, Published: { TranslationVersion: { Length: > 0 } version } published })
        {
            // Nothing printed at the title screen: chat does not exist there, and the line would be
            // spent on nobody. The Login handler comes back to this the moment it does exist.
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
        // which are the same silence from its side and two different sentences from the user's.
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

    /// <summary>
    ///     One line of chat, built here and printed where the game expects to be spoken to.
    /// </summary>
    /// <remarks>
    ///     The clickable part is what somebody is going to try to click anyway: the alternative is a
    ///     line that names <c>/gubal</c> and asks them to type it. Both are in there, since a chat log
    ///     scrolled past a link still has to be actionable.
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
    /// <remarks>
    ///     Both callers reach here off a background task most of the time — a check finishing — and on
    ///     the framework thread the rest of it. Chat has tolerated being written to from elsewhere,
    ///     which is a poor reason to keep doing it.
    /// </remarks>
    private void Print(Action print)
    {
        // A check outlives the plugin whenever somebody reloads it while one is in flight, which on a
        // dev build is most of them. There is nobody left to tell by then, and the queue that would
        // carry the message is being torn down.
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
    ///     Someone who has turned the pack off, or has installed one and not restarted yet, should
    ///     still be told that a newer one exists — those are the states where they are most likely to
    ///     be about to act on it.
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


