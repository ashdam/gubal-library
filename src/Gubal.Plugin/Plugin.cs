using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gubal";

    private const uint OpenConfigLinkId = 1;

    private static readonly TimeSpan BootUpdateBudget = TimeSpan.FromMinutes(2);

    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly FileDialogManager fileDialogs = new();
    private readonly Configuration config;
    private readonly CancellationTokenSource usageCancellation = new();
    private Task? usageTask;
    private readonly UsageOutbox usageOutbox;
    private readonly ConfigWindow configWindow;
    private readonly LobbyUpdateNotice lobbyUpdateNotice;
    private readonly DialogueCodex dialogueCodex;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly bool waitedForPluginsAtBoot;

    private ExdRedirector? redirector;
    private readonly string? redirectorError;

    private readonly ShadowState? shadow;

    private bool shadowAnnounced;

    private readonly PackInstaller installer;
    private readonly PackOperations operations = null!;
    private volatile bool disposed;

    private PackContents? contents;

    private string contentsPath = string.Empty;

    private string? manifestPath;
    private PackManifest? installedManifest;

    private SqPackProbe? probe;
    private readonly IGameInteropProvider interop;
    private readonly WindowSystem windows = new("GubalLibrary");

    private readonly DalamudLinkPayload openConfigLink;

    private UpdateStatus update;

    private readonly string? bootUpdate;

    private bool bootUpdateAnnounced;

    private bool checking;

    private string? announcedVersion;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IChatGui chat,
        IClientState clientState,
        IFramework framework,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        ITextureProvider textures,
        IDataManager data,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog log)
    {
        this.usageOutbox = new UsageOutbox(Path.Combine(pluginInterface.GetPluginConfigDirectory(),
            UsageReporter.Endpoint.IsLoopback ? "usage-pending-local" : "usage-pending"));
        log = new ReportingLog(log, this.usageOutbox.Errors);
        Language.Apply(pluginInterface.UiLanguage, log);

        this.pluginInterface = pluginInterface;
        this.lobbyUpdateNotice = new LobbyUpdateNotice(pluginInterface, textures, log);
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        this.clientState = clientState;
        this.framework = framework;
        // Changing the wait setting cannot pause a game that has already started.
        this.waitedForPluginsAtBoot = pluginInterface.Reason == PluginLoadReason.Boot
            && DalamudBootWait.IsOn(pluginInterface) is true;

        try
        {
            this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            if (!DalamudBootWait.EnsureOn(pluginInterface))
                log.Warning("Could not enable startup waiting. Enable 'Wait for plugins before game loads' in Dalamud settings.");

            if (this.config.LanguagePackPath.Length > 0 && this.config.InstalledFrom.Length == 0)
            {
                this.config.InstalledFrom = this.config.PackSource;
            }

            if (this.config.OwnPackFolder.Length == 0
                && this.config.PackSource.Length > 0
                && !PackInstaller.IsRemote(this.config.PackSource)
                && !this.config.PackSource.Trim().EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                this.config.OwnPackFolder = this.config.PackSource;
            }

            Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

            this.interop = interop;
            Diagnostics.Enabled = this.config.Debug;
            this.probe = this.config.Debug ? new SqPackProbe(interop, log) : null;

            this.installer = DalamudPackInstaller.Create(log, pluginInterface.GetPluginConfigDirectory());

            if (this.waitedForPluginsAtBoot)
            {
                var activated = this.installer.ActivatePending(this.config, this.SaveConfig);
                if (activated is { Success: false } failure)
                    log.Error("The pending pack was not activated: {Error}", failure.Error ?? "Unknown activation error.");
                else if (activated is { Success: true, Manifest: { } manifest })
                    this.bootUpdate = manifest.TranslationVersion;
            }
            if (!this.installer.HasPending)
                this.bootUpdate = this.UpdateBeforeTheGameReads(log) ?? this.bootUpdate;

            if (this.config.ServeLanguagePack && this.config.LanguagePackPath.Length > 0)
            {
                // Install before the client caches its Excel pages.
                (this.redirector, this.redirectorError) = ExdRedirector.Create(
                    interop,
                    sigScanner,
                    log,
                    this.config.LanguagePackPath,
                    this.Contents(),
                    this.config.DisabledSheets,
                    this.config.LifestreamCompatibility,
                    data,
                    Path.Combine(pluginInterface.GetPluginConfigDirectory(), "compatibility-cache"),
                    () => this.waitedForPluginsAtBoot);

                if (this.redirectorError is { Length: > 0 } error)
                {
                    this.usageOutbox.Errors.Capture("redirector", "pack-load-failed", error);
                    log.Warning("Translated pages are not being served: {Error}", error);
                }

                // Only packs accepted for the game can be served to other plugins.
                if (this.redirector is not null)
                {
                    this.shadow = GubalLumina.Install(
                        data,
                        this.redirector.ServedPages,
                        GubalLumina.Folder(pluginInterface.GetPluginConfigDirectory()),
                        log);

                    // The game and other plugins must receive the same selected pages.
                    if (!this.shadow.Ok)
                    {
                        this.usageOutbox.Errors.Capture("shared-sheets", "pack-load-failed", this.shadow.Message);
                        this.redirector.Dispose();
                        this.redirector = null;
                        log.Warning("[shadow] the pack was withdrawn from the client too, rather than serve half");
                    }

                    Diagnostics.Log(log, $"[shadow] {this.shadow.Message} [{this.shadow.Folder}]");
                }
            }

            this.dialogueCodex = new DialogueCodex(this.config, data, gameGui, clientState,
                addonLifecycle, framework, log, sigScanner, pluginInterface.UiBuilder);

            this.operations = new PackOperations(this.installer, framework, log);
            this.configWindow = new ConfigWindow(
                this.config,
                this.SaveConfig,
                this.fileDialogs,
                this.PageSnapshot,
                this.Contents,
                textures,
                data,
                gameGui,
                this.operations,
                this.OnPackPrepared,
                () => this.BeginUpdateCheck(verbose: false),
                this.SetAutoUpdate,
                () => DalamudBootWait.IsOn(this.pluginInterface),
                () => pluginInterface.OpenDalamudSettingsTo(SettingsOpenKind.General),
                () => this.shadow,
                pluginInterface.Manifest.AssemblyVersion.ToString(),
                () => this.clientState.IsLoggedIn);

            this.configWindow.ConfigureActionFont(pluginInterface.UiBuilder);
            this.configWindow.ConfigureDevPreview(addonLifecycle, sigScanner, framework);
            this.windows.AddWindow(this.configWindow);

            this.openConfigLink = chat.AddChatLinkHandler(OpenConfigLinkId, (_, _) => this.OpenConfig());

            this.clientState.Login += this.OnLogin;
            pluginInterface.LanguageChanged += this.OnLanguageChanged;

            this.BeginUpdateCheck(verbose: false);

            this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open settings. See the plugin description for subcommands.",
            });

            pluginInterface.UiBuilder.Draw += this.DrawUi;
            pluginInterface.UiBuilder.DisableCutsceneUiHide = true;
            pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
            pluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;

            this.ReportStartupUsage();
        }
        catch (Exception error)
        {
            this.usageOutbox.Errors.Capture("startup", "initialization-failed", "Plugin initialization failed.", error);
            try { this.Dispose(); }
            catch (Exception cleanupError) { log.Error(cleanupError, "Could not release all plugin resources after initialization failed."); }
            throw;
        }
    }

    private void ReportStartupUsage()
    {
        // A reload cannot establish which cached text the game uses. Wait for a game restart.
        if (this.pluginInterface.Reason != PluginLoadReason.Boot)
            return;
        try
        {
            var manifest = this.redirector?.Manifest ?? this.InstalledManifest();
            var official = UsageSnapshot.OfficialPack(this.config.InstalledFrom,
                string.Equals(this.config.LanguagePackPath, this.installer.InstalledPath,
                    StringComparison.OrdinalIgnoreCase),
                KnownPacks.All.Select(pack => (pack.Code, pack.Source)));
            var snapshot = UsageSnapshot.Create(official, manifest?.TranslationVersion,
                this.config.LanguagePackPath.Length > 0, manifest is not null,
                this.config.ServeLanguagePack, this.redirector is not null,
                manifest is not null && PackVersion.Error(manifest.GameVersion,
                    ExdRedirector.RunningGameVersion()) is null);
            if (this.config.UsageInstallationId == Guid.Empty)
                this.config.UsageInstallationId = Guid.NewGuid();
            this.config.UsageSequence = checked(this.config.UsageSequence + 1);
            this.SaveConfig(this.config);
            var report = UsageReport.Create(this.config.UsageInstallationId, this.config.UsageSequence,
                this.pluginInterface.Manifest.AssemblyVersion.ToString(), snapshot);
            var cancel = this.usageCancellation.Token;
            var outbox = this.usageOutbox;
            // Capture the session before an install or a selection can change the configuration.
            this.usageTask = Task.Run(async () =>
            {
                try
                {
                    await outbox.EnqueueAsync(report).ConfigureAwait(false);
                    using var client = new System.Net.Http.HttpClient(
                        new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
                    await outbox.SendAsync(client, UsageReporter.Endpoint, cancel,
                        message => Diagnostics.Log(this.log, message)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Diagnostics.Log(this.log, "Startup usage reporting failed.");
                }
            });
        }
        catch (Exception)
        {
            Diagnostics.Log(this.log, "Startup usage reporting could not be prepared.");
        }
    }

    private void DrawUi()
    {
        try
        {
            this.dialogueCodex.DrawLinks();
            if (!this.pluginInterface.UiBuilder.CutsceneActive)
                this.configWindow.PreviewPluginUpdate = this.lobbyUpdateNotice.Draw(
                    this.clientState.IsLoggedIn, this.configWindow.DevToolsEnabled && this.configWindow.PreviewPluginUpdate);
            this.windows.Draw();
            this.fileDialogs.Draw();
        }
        catch (Exception error)
        {
            this.log.Error(error, "Could not draw the plugin interface.");
            throw;
        }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        try
        {
            this.operations?.Dispose();
            this.usageCancellation.Cancel();
            if (this.usageTask is { } pendingUsage)
                _ = pendingUsage.ContinueWith(_ => this.usageCancellation.Dispose(), TaskScheduler.Default);
            else
                this.usageCancellation.Dispose();
            this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
            this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
            this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenConfig;

            this.clientState.Login -= this.OnLogin;
            this.pluginInterface.LanguageChanged -= this.OnLanguageChanged;
            this.chat.RemoveChatLinkHandler();

            this.commands.RemoveHandler(CommandName);
            this.fileDialogs.Reset();
            this.dialogueCodex?.Dispose();

            this.configWindow?.DisposeDevPreview();
            this.windows.RemoveAllWindows();
            this.configWindow?.DisposeActionFont();
        }
        finally
        {
            this.redirector?.Dispose();
            this.probe?.Dispose();
        }
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

            case "parts":
                this.PrintParts();
                break;

            case "check":
                this.chat.Print(this.BeginUpdateCheck(verbose: true)
                    ? "[Gubal]Asking whether a newer language pack is published..."
                    // Not "the answer follows": the check already running is the quiet one from load.
                    : "[Gubal]A check is already running, and /gubal shows what it comes back with.");
                break;

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

                this.chat.Print(DalamudBootWait.IsOn(this.pluginInterface) is true
                    ? "[Gubal]Startup updates ON. Dalamud will hold the game's start while a newer pack is fetched."
                    : "[Gubal]Startup updates ON, but Dalamud is not waiting for plugins before the game loads "
                      + "and could not be set to. Turn it on in Dalamud's settings or this does nothing.");
                break;

            case "dev":
                this.chat.Print(this.configWindow.ToggleDevTab()
                    ? "[Gubal]Dev tab enabled."
                    : "[Gubal]Dev tab disabled.");
                this.OpenConfig();
                break;

            case "debug":
            case "debug on":
            case "debug off":
                this.config.Debug = arguments.Trim() == "debug"
                    ? !Diagnostics.Enabled
                    : arguments.Trim().EndsWith("on", StringComparison.OrdinalIgnoreCase);
                Diagnostics.Enabled = this.config.Debug;
                if (Diagnostics.Enabled)
                    this.probe ??= new SqPackProbe(this.interop, this.log);
                else
                {
                    this.probe?.Dispose();
                    this.probe = null;
                }
                this.SaveConfig(this.config);
                this.chat.Print(this.config.Debug
                    ? "[Gubal]Debug ON. Probes are active. /gubal debug off"
                    : "[Gubal]Debug OFF. Probes are inactive.");
                break;

            default:
                this.chat.Print("[Gubal]Usage: /gubal [status|parts|check|usepack|autoupdate|dev|debug on|debug off]");
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
                $"[Gubal]{pages.PageCount:N0} page(s), built for game {pack.GameVersion ?? "unknown"}.");

            this.chat.Print($"[Gubal]{pages.ServedCount:N0} read(s) answered from disk this session.");

            if (pages.FontCount > 0)
            {
                this.chat.Print(
                    $"[Gubal]{pages.FontsServedCount:N0} of {pages.FontCount:N0} font file(s) served from the pack.");
            }

            this.chat.Print(this.shadow is { Ok: true } served
                ? $"[Gubal]Other plugins read the pack too, from {served.Folder}."
                : $"[Gubal]Other plugins read the game{(char)39}s own text: {this.shadow?.Message ?? "no pack is being served"}.");

            // Otherwise somebody reports a bug about text left untranslated exactly as they asked.
            if (this.Contents().PartsOff(this.config.DisabledSheets) is { Count: > 0 } off)
            {
                this.chat.Print($"[Gubal]Switched off on purpose, so still the game's own text: {string.Join(", ", off)}.");
            }

            return;
        }

        this.chat.PrintError(
            $"[Gubal]No pages served. {pages.Error ?? "no language pack configured."}");
    }

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

        this.chat.Print("[Gubal]Change these under Localization settings in /gubal. They take effect at the next start.");
        if (this.config.LifestreamCompatibility)
        {
            this.chat.Print("[Gubal]Lifestream compatibility is selected. Its required sheets stay in English after restart, even when their parts are on.");
        }
    }

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
            ? new PageStatus(
                true, r.PageCount, r.ServedCount, r.FontCount, r.FontsServedCount, null, r.Manifest, this.update, r.Compatibility)
            : new PageStatus(false, 0, 0, 0, 0, this.redirectorError, this.InstalledManifest(), this.update);
    }

    private void OnPackPrepared()
    {
        this.chat.Print(DalamudBootWait.IsOn(this.pluginInterface) is true
            ? "[Gubal]Language pack prepared. Restart the game to activate it."
            : "[Gubal]Language pack prepared. Enable 'Wait for plugins before game loads' in Dalamud settings to continue.");
    }

    private bool BeginUpdateCheck(bool verbose)
    {
        if (this.disposed || this.checking)
        {
            return false;
        }

        this.checking = true;

        this.update = default;

        var installed = this.redirector?.Manifest ?? this.InstalledManifest();
        this.operations.Check(installed, ExdRedirector.RunningGameVersion(), result =>
        {
            this.update = result;
            this.checking = false;
            this.Announce(verbose);
        });
        return true;
    }

    // Startup updates must finish while Dalamud holds the game boot.
    private string? UpdateBeforeTheGameReads(IPluginLog log)
    {
        if (this.pluginInterface.Reason != PluginLoadReason.Boot
            || !this.config.AutoUpdatePack
            || !this.config.ServeLanguagePack
            || !PackInstaller.IsRemote(this.config.PackSource)
            || !string.Equals(
                this.config.LanguagePackPath, this.installer.InstalledPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!this.waitedForPluginsAtBoot)
        {
            log.Warning(
                "Skipping the startup pack update: Dalamud was not waiting for plugins when this session started. "
                + "Startup waiting must be enabled before restarting the game.");
            return null;
        }

        try
        {
            using var budget = new CancellationTokenSource(BootUpdateBudget);
            return Task.Run(() => this.TakeUpdateAsync(log, budget.Token)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            log.Error(e, "Updating the language pack during startup failed.");
            return null;
        }
    }

    private async Task<string?> TakeUpdateAsync(IPluginLog log, CancellationToken cancel)
    {
        var installed = PackManifest.Read(this.config.LanguagePackPath).Manifest;

        if (await this.installer.CheckForUpdateAsync(installed, cancel, ExdRedirector.RunningGameVersion()).ConfigureAwait(false)
            is not { State: UpdateState.Available, Published: { } published })
        {
            return null;
        }

        // Automatic updates require the current game version.
        var version = published.TranslationVersion ?? "unversioned";
        var running = ExdRedirector.RunningGameVersion();

        var builtFor = published.GameVersion;
        if (PackVersion.Error(builtFor, running) is not null)
        {
            log.Warning(
                "Not taking language pack {Version}: it is built for game {BuiltFor} and this client "
                + "runs {Running}. The installed pack is left alone.",
                version,
                builtFor ?? "an unknown version",
                running ?? "an unknown version");
            return null;
        }

        Diagnostics.Log(log, "Taking language pack {Version} before the game reads its text.", version);

        var result = await this.installer.InstallAsync(this.config.PackSource, null, cancel).ConfigureAwait(false);

        if (!result.Success)
        {
            log.Error(
                "The startup update did not happen: {Error} The installed pack is untouched.",
                result.Error ?? "no reason given.");
            return null;
        }

        return result.Manifest?.TranslationVersion ?? "unversioned";
    }

    private void SetAutoUpdate(bool value)
    {
        this.config.AutoUpdatePack = value;

        this.SaveConfig(this.config);
    }

    private void Announce(bool verbose)
    {
        var status = this.update;

        if (status is { State: UpdateState.Available, Published: { TranslationVersion: { Length: > 0 } version } published })
        {
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

    private void OnLanguageChanged(string code)
    {
        Language.Apply(code, this.log);
        PackParts.Invalidate();
        this.contents = null;
    }

    private void OnLogin()
    {
        if (this.bootUpdate is { Length: > 0 } version && !this.bootUpdateAnnounced)
        {
            this.bootUpdateAnnounced = true;
            this.chat.Print($"[Gubal]Language pack updated to {version} before this session started.");
        }

        if (this.shadow is { } state && !this.shadowAnnounced)
        {
            this.shadowAnnounced = true;
            if (!state.Ok)
            {
                this.chat.PrintError(new SeStringBuilder()
                    .AddText($"[Gubal]Nothing is translated this session. {state.Message} ")
                    .Add(this.openConfigLink)
                    .AddUiForeground(539)
                    .AddText("Open the settings")
                    .AddUiForegroundOff()
                    .Add(RawPayload.LinkTerminator)
                    .AddText(" to see it again, and please report it.")
                    .Build());
            }
            else
            {
                this.chat.Print(new SeStringBuilder()
                    .AddText("[Gubal]Other plugins read the translation too. ")
                    .AddUiForeground(539)
                    .AddText("If one of them starts behaving oddly, please say which one")
                    .AddUiForegroundOff()
                    .AddText(", that is a bug worth fixing rather than living with.")
                    .Build());
            }
        }

        this.Announce(verbose: false);
    }

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
            .AddText(" or type /gubal to install it. The game has to restart afterwards.")
            .Build();
    }

    // Queued callbacks must not outlive this plugin instance.
    private void Print(Action print)
    {
        if (this.disposed || this.framework.IsFrameworkUnloading)
        {
            return;
        }

        if (this.framework.IsInFrameworkUpdateThread)
        {
            print();
            return;
        }

        _ = this.framework.RunOnFrameworkThread(() => { if (!this.disposed) print(); });
    }

    private PackManifest? InstalledManifest()
    {
        var path = this.config.LanguagePackPath;
        if (!string.Equals(this.manifestPath, path, StringComparison.OrdinalIgnoreCase))
        {
            this.installedManifest = path.Length > 0 ? PackManifest.Read(path).Manifest : null;
            this.manifestPath = path;
        }

        return this.installedManifest;
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
