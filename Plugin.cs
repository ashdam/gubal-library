using Dalamud.Game.Command;
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

    private readonly IChatGui chat;
    private readonly ICommandManager commands;
    private readonly FileDialogManager fileDialogs = new();
    private readonly Configuration config;
    private readonly ConfigWindow configWindow;
    private readonly IDalamudPluginInterface pluginInterface;

    private readonly ExdRedirector? redirector;
    private readonly string? redirectorError;

    private readonly SqPackProbe? probe;
    private readonly WindowSystem windows = new("GubalLibrary");

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IChatGui chat,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.chat = chat;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (this.config.Migrate())
        {
            log.Information("Carried the configured language pack over from the previous setting names.");
            this.SaveConfig(this.config);
        }

        Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

        // Attached first, before anything else, because what it measures is how early this plugin
        // runs. Anything queued ahead of it would be measuring itself.
        this.probe = this.config.ProbeSqPack ? new SqPackProbe(interop, log) : null;

        // Installed here, in the constructor, and nowhere else. This is the whole reason the route
        // works: the client reads its sheets about two seconds after plugins load and keeps them for
        // the session, so a redirection put in place any later is invisible for everything already
        // read. Measured, and it is why the guildhest descriptions once stayed English for a session.
        if (this.config.ServeLanguagePack && this.config.LanguagePackPath.Length > 0)
        {
            (this.redirector, this.redirectorError) =
                ExdRedirector.Create(interop, log, this.config.LanguagePackPath);

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
            pluginInterface.Manifest.AssemblyVersion.ToString());

        this.windows.AddWindow(this.configWindow);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open settings. Subcommands: status, usepack, probesqpack",
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
                this.chat.Print("[Gubal]Usage: /gubal [status|usepack|probesqpack]");
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
            return;
        }

        this.chat.PrintError(
            $"[Gubal]No pages served — {pages.Error ?? "no language pack configured."}");
    }

    /// <summary>
    ///     What the settings window and <c>/gubal status</c> report about the file route.
    /// </summary>
    /// <remarks>
    ///     Both counts, deliberately. How many pages are registered says the folder was read; how
    ///     many reads were answered says the game is actually taking them, and only the second one
    ///     distinguishes a working route from a route that was installed too late to matter.
    /// </remarks>
    private PageStatus PageSnapshot()
    {
        return this.redirector is { } r
            ? new PageStatus(true, r.PageCount, r.ServedCount, null, r.Manifest)
            : new PageStatus(false, 0, 0, this.redirectorError, null);
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


