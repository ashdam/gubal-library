using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace GubalLibrary;

/// <summary>
///     The whole user interface: where the pages are, whether they are reaching the game, and what
///     they contain.
/// </summary>
/// <remarks>
///     Ordered by what a new user has to do, not by what the plugin does internally. The plugin
///     ships no translations, so a fresh install can do exactly one useful thing — be pointed at a
///     folder — and that row is therefore first, under whatever the status line has to say about it.
///     Everything below it only has meaning once that folder exists.
/// </remarks>
internal sealed class ConfigWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);

    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<PageStatus> pageStatus;
    private readonly PackInstaller installer;

    private volatile bool installing;
    private string? installMessage;
    private bool installFailed;

    /// <param name="version">The plugin's own version, shown in the title bar.</param>
    /// <remarks>
    ///     The window id is pinned with <c>###</c> so the title can carry the version without ImGui
    ///     treating each build as a different window and forgetting its size and position.
    /// </remarks>
    public ConfigWindow(
        Configuration config,
        Action<Configuration> save,
        FileDialogManager fileDialogs,
        Func<PageStatus> pageStatus,
        PackInstaller installer,
        string version)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.config = config;
        this.save = save;
        this.fileDialogs = fileDialogs;
        this.pageStatus = pageStatus;
        this.installer = installer;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 220),
            MaximumSize = new Vector2(900, 800),
        };
    }

    public override void Draw()
    {
        var pages = this.pageStatus();
        var changed = false;

        this.DrawHeadline(pages);
        this.DrawUpdateNotice(pages);
        ImGui.Spacing();

        // First, because on a fresh install it is the only thing that can be done and everything
        // else on screen is a consequence of it.
        this.DrawLanguagePackRow(ref changed);

        if (pages.Manifest is { } pack)
        {
            ImGui.Separator();
            DrawPackDetail(pack, pages);
        }

        ImGui.Separator();
        this.DrawDiagnostics(ref changed);

        if (changed)
        {
            this.save(this.config);
        }
    }

    /// <summary>
    ///     One line at the top saying whether another language is actually reaching the game.
    /// </summary>
    /// <remarks>
    ///     Recomputed every frame rather than cached, because the interesting part of it changes
    ///     while the window is open. The distinction between <em>loaded</em> and <em>read from</em>
    ///     is carried in the colour and it is not a nicety: a route installed too late to matter
    ///     looks, on every other indicator, exactly like one that is working, and confusing those two
    ///     states cost a full session of testing.
    /// </remarks>
    private void DrawHeadline(PageStatus pages)
    {
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

            { Error: { Length: > 0 } error } => (Red, FontAwesomeIcon.ExclamationTriangle, error),

            _ => (
                Amber,
                FontAwesomeIcon.ExclamationTriangle,
                "NO LANGUAGE PACK LOADED. This plugin ships no translations — install one, point at it below, tick the box, and restart the client."),
        };

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        ImGui.SameLine();
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     Says whether the installed pack can keep itself current, and never acts on the answer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A newer version is offered rather than applied, even though this plugin could obviously
    ///         just fetch it. Taking the update means downloading tens of megabytes and then
    ///         restarting the client, and doing that to somebody who sat down to play is not an
    ///         improvement however new the translation is. The check costs two kilobytes and runs by
    ///         itself; the twenty megabytes are a decision.
    ///     </para>
    ///     <para>
    ///         A pack that <em>cannot</em> update is said out loud too, whether because it declares no
    ///         address or because the address it declares has gone quiet. Those differ in blame and
    ///         not in effect: either way the translation on screen is the last one this person will
    ///         ever see unless they go looking, and that is worth knowing before they wonder for
    ///         months why a line they reported is still wrong.
    ///     </para>
    /// </remarks>
    private void DrawUpdateNotice(PageStatus pages)
    {
        // Nothing to say about updating something that is not there. The headline above already says
        // no pack is loaded, and following it with "and it will never update" reads as a second,
        // separate fault.
        if (pages.Manifest is null)
        {
            return;
        }

        switch (pages.Update.State)
        {
            case UpdateState.Available when pages.Update.Published is { } update:
                Icon(FontAwesomeIcon.ArrowUp, Amber);
                ImGui.TextWrapped(
                    $"A newer language pack is published: {update.TranslationVersion}"
                    + (update.GameVersion is { Length: > 0 } game ? $", built for game {game}" : string.Empty));
                ImGui.PopStyleColor();

                using (ImRaii.Disabled(this.installing || this.config.PackSource.Trim().Length == 0))
                {
                    // Reinstalls from where this one came from. There is no address in the manifest
                    // to prefer, on purpose: the pack does not repeat a fact the user already
                    // supplied, and successive versions are expected at a stable address.
                    if (ImGui.Button("Update##pack"))
                    {
                        this.Install(this.config.PackSource);
                    }
                }

                break;

            // Both halves of "this pack will not improve on its own" get said, because from where the
            // player sits the consequence is the same and only the wording should differ. Never
            // reached while the pack is merely being checked: that state is the enum's default for
            // exactly this reason.
            // Red, and short. It is a failure of something that was promised, and the reason it
            // failed belongs in the log rather than on screen: the exception text runs to a line and
            // a half of Winsock, which buries the one sentence that tells the reader what to do.
            case UpdateState.Unreachable:
                Icon(FontAwesomeIcon.ExclamationTriangle, Red);
                ImGui.TextWrapped(
                    "No connection to the language pack update URL. If it persists, this pack will "
                    + "not update itself — check where you got it from.");
                ImGui.PopStyleColor();
                break;

            // Amber, not red: nothing is broken. The pack simply never offered to keep itself
            // current, which is a limitation to know about rather than a fault to chase.
            case UpdateState.NotDeclared:
                Icon(FontAwesomeIcon.ExclamationTriangle, Amber);
                ImGui.TextWrapped(
                    "This language pack has no update URL, so it will never update itself.");
                ImGui.PopStyleColor();
                break;
        }
    }

    /// <summary>Draws a coloured icon and leaves the colour pushed for the text that follows.</summary>
    private static void Icon(FontAwesomeIcon icon, Vector4 colour)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        ImGui.SameLine();
    }

    /// <summary>
    ///     Where the language pack is, and the switch that turns it on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>"Language pack", never "pages".</b> A page is an <c>.exd</c> file, which is this
    ///         project's vocabulary and not a user's; what somebody installs is a language. The
    ///         distinction is not pedantry — it is what the folder will stop being, since the intent
    ///         is to ship a pack as a single zip holding the same files and the same manifest, at
    ///         which point "page folder" would name an implementation detail that had gone away.
    ///     </para>
    ///     <para>
    ///         A checkbox rather than a Serve button, and it says so, because there is nothing this
    ///         can do now. The game reads its sheets once, seconds into startup, and caches them for
    ///         the session; the redirection has to be in place before that or it may as well not
    ///         exist. So this decides what happens at the <em>next</em> start, and a button labelled
    ///         Serve that changed nothing visible was read — correctly — as a broken button.
    ///     </para>
    /// </remarks>
    private void DrawLanguagePackRow(ref bool changed)
    {
        var source = this.config.PackSource;

        // Aligned to the frame padding, not drawn at the raw cursor: text placed beside an input box
        // sits at the top of it otherwise, a couple of pixels above the text inside the box.
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Language pack");
        ImGui.SameLine();

        // Negative width, so the box gives back a fixed strip to the buttons on its right and takes
        // whatever is left of the row. Both ends stay put as the window resizes.
        ImGui.SetNextItemWidth(-150f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##packSource", ref source, 2048))
        {
            this.config.PackSource = source;
            changed = true;
        }

        SetTooltip("A .zip, a link to one, or a folder that has already been unpacked.\n"
                   + "Whatever it is must contain gubal-manifest.json, which says what the\n"
                   + "pack is and which game version it was built for.");

        ImGui.SameLine();
        if (ImGui.Button("Browse...##packSource"))
        {
            this.BrowseForLanguagePack();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(this.installing || this.config.PackSource.Trim().Length == 0))
        {
            if (ImGui.Button("Install##packSource"))
            {
                this.Install(this.config.PackSource);
            }
        }

        SetTooltip("Downloads and unpacks it if it needs it, then serves it from the next start.\n"
                   + "Nothing is fetched unless you press this.");

        var serve = this.config.ServeLanguagePack;
        using (ImRaii.Disabled(this.config.LanguagePackPath.Length == 0))
        {
            if (ImGui.Checkbox("Use this language pack from the next start", ref serve))
            {
                this.config.ServeLanguagePack = serve;
                changed = true;
            }
        }

        SetTooltip("Gives the game the pack's text instead of its own.\n"
                   + "Takes effect when the client next starts: the game reads its text once\n"
                   + "at startup and keeps it for the session, so this cannot be switched on mid-game.");

        if (this.installing)
        {
            ImGui.TextDisabled("Installing...");
        }
        else if (this.installMessage is { Length: > 0 } message)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, this.installFailed ? Red : Green);
            ImGui.TextWrapped(message);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    ///     Runs the install off the UI thread and reports the outcome back into the window.
    /// </summary>
    /// <remarks>
    ///     ImGui redraws every frame from the game's own update, so doing this inline would freeze
    ///     the client for the length of a download. The three fields it writes are only ever read by
    ///     <see cref="Draw" />, a frame or more later, which is why they need no synchronisation
    ///     beyond being set in this order.
    /// </remarks>
    private void Install(string source)
    {
        this.installing = true;
        this.installMessage = null;

        _ = Task.Run(async () =>
        {
            var result = await this.installer.InstallAsync(source).ConfigureAwait(false);

            if (result.Success)
            {
                this.config.LanguagePackPath = result.Path;
                this.config.ServeLanguagePack = true;
                this.save(this.config);

                var pack = result.Manifest!;
                this.installFailed = false;
                this.installMessage =
                    $"Installed {pack.DisplayName} ({pack.TranslationVersion ?? "unversioned"}). "
                    + "RESTART THE CLIENT to see it — the game reads its text once at startup.";
            }
            else
            {
                this.installFailed = true;
                this.installMessage = result.Error;
            }

            this.installing = false;
        });
    }

    /// <summary>What the loaded pack is, who made it, and how much of it is translated.</summary>
    private static void DrawPackDetail(PackManifest pack, PageStatus pages)
    {
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
                + $"across {pack.Pages:N0} page(s), in the sheets the pack covers");
        }

        ImGui.TextDisabled($"Built for game {pack.GameVersion ?? "unknown"}");

        if (pages.Active)
        {
            ImGui.TextDisabled($"{pages.ServedCount:N0} read(s) answered from disk this session");
        }
    }

    /// <summary>
    ///     Collapsed by default, because nothing here is part of using the plugin.
    /// </summary>
    /// <remarks>
    ///     The probe hooks a second function on the file read path and writes a line per Excel page
    ///     to the log. That is a real cost for a real question — has a patch or a settings change
    ///     eaten the margin this plugin needs to attach before the client's boot reads — and no cost
    ///     anybody should pay by accident.
    /// </remarks>
    private void DrawDiagnostics(ref bool changed)
    {
        if (!ImGui.CollapsingHeader("Diagnostics"))
        {
            return;
        }

        var probe = this.config.ProbeSqPack;
        if (ImGui.Checkbox("Log every Excel page the game reads", ref probe))
        {
            this.config.ProbeSqPack = probe;
            changed = true;
        }

        SetTooltip("Writes one line per page to /xllog, redirecting nothing.\n"
                   + "Attaches at load, so it takes effect on the next client start.");
    }

    /// <summary>
    ///     Picks a <c>.zip</c> or an already-unpacked folder, and fills the source box with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A file dialog rather than a folder one, with a filter that admits both. Browse cannot
    ///         offer a URL, so restricting it to folders would have made the commonest local case —
    ///         somebody who has just downloaded a zip — the one case the button could not help with.
    ///     </para>
    ///     <para>
    ///         Fills the box and stops there. Installing from a path the moment it is picked would
    ///         start a download or unpack thousands of files on a single click, before the person has
    ///         had a chance to read what they picked.
    ///     </para>
    /// </remarks>
    private void BrowseForLanguagePack()
    {
        var startPath = this.config.LanguagePackPath;
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            startPath = string.Empty;
        }

        this.fileDialogs.OpenFileDialog(
            "Select a language pack (.zip) or an unpacked folder",
            ".zip,.*",
            (confirmed, selected) =>
            {
                if (!confirmed || selected.Count == 0 || string.IsNullOrWhiteSpace(selected[0]))
                {
                    return;
                }

                this.config.PackSource = selected[0];
                this.save(this.config);
            },
            selectionCountMax: 1,
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

/// <param name="Active">The redirection is installed and holding pages.</param>
/// <param name="PageCount">How many pages it would answer for.</param>
/// <param name="ServedCount">How many reads it has actually answered — the number that proves it.</param>
/// <param name="Error">Why it is not installed, when it is not. Null when it is, or when nobody asked.</param>
/// <param name="Manifest">What the loaded pack says about itself. Null when none loaded.</param>
/// <param name="Update">What the background check made of the pack's declared update address.</param>
internal readonly record struct PageStatus(
    bool Active, int PageCount, int ServedCount, string? Error, PackManifest? Manifest, UpdateStatus Update);





