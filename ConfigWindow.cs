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
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<PageStatus> pageStatus;
    private readonly PackInstaller installer;

    private readonly Action onPackInstalled;

    /// <summary>Asks the plugin to put the update question again. Answered a second or so later.</summary>
    /// <remarks>
    ///     The plugin's job rather than this window's, because the manifest to ask about depends on
    ///     what is being served and what is merely installed — which is a fact the window has no
    ///     business learning to compute a second time.
    /// </remarks>
    private readonly Action checkForUpdate;

    private volatile bool installing;
    private InstallProgress progress;
    private string? installMessage;
    private bool installFailed;

    /// <summary>Set once a pack has been installed and not yet picked up. Never cleared.</summary>
    /// <remarks>
    ///     Only a restart clears it, because only a restart acts on it: the client reads its text
    ///     once at startup, so between installing and restarting the game is still showing the
    ///     previous pack and there is nothing the plugin can do about that.
    /// </remarks>
    private bool restartPending;

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
        Action onPackInstalled,
        Action checkForUpdate,
        string version)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.config = config;
        this.save = save;
        this.fileDialogs = fileDialogs;
        this.pageStatus = pageStatus;
        this.installer = installer;
        this.onPackInstalled = onPackInstalled;
        this.checkForUpdate = checkForUpdate;

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

        // Suppressed once something has been installed, because everything it could say is about the
        // pack that is on its way out. Whether the OLD pack has a newer version published stopped
        // being anybody's problem the moment a new one was put in its place.
        if (!this.restartPending)
        {
            this.DrawUpdateNotice(pages);
        }

        this.DrawRestartBanner();
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
                // Three steps, not four. It used to say "tick the box" as well, which is work the
                // Install button already does — and an instruction that asks for something already
                // done reads as a step that did not take.
                "NO LANGUAGE PACK LOADED. This plugin ships no translations — put a link or a folder "
                + "below, press Install, and restart the client."),
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
    ///     <para>
    ///         <b>Every state draws something, including the two that once drew nothing.</b> That was
    ///         fine while the check ran by itself and unseen; with a button beside it, a press that
    ///         leaves the window exactly as it was is indistinguishable from a button that does not
    ///         work — and "up to date" is the answer somebody opening this window came for.
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

        var checking = pages.Update.State == UpdateState.Checking;

        switch (pages.Update.State)
        {
            // The enum's default, and so also what is shown for the seconds between asking and being
            // answered — at load, and again every time the button below is pressed.
            case UpdateState.Checking:
                Icon(FontAwesomeIcon.Hourglass, Grey);
                ImGui.TextWrapped("Checking whether a newer language pack is published...");
                ImGui.PopStyleColor();
                break;

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

                ImGui.SameLine();
                break;

            // Green and quiet. It says nothing the player has to act on, and its whole job is to be
            // the visible difference between a check that came back clean and one that never ran.
            case UpdateState.UpToDate:
                Icon(FontAwesomeIcon.Check, Green);
                ImGui.TextWrapped(
                    $"Up to date: {pages.Manifest.TranslationVersion ?? "unversioned"} is the latest published.");
                ImGui.PopStyleColor();
                break;

            // Both halves of "this pack will not improve on its own" get said, because from where the
            // player sits the consequence is the same and only the wording should differ.
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

                // No button: there is no address to ask, so the only honest thing a Check here could
                // do is come straight back with the sentence above.
                return;
        }

        using (ImRaii.Disabled(checking))
        {
            if (ImGui.Button("Check for updates##pack"))
            {
                this.checkForUpdate();
            }
        }

        SetTooltip("Asks the address inside the installed pack whether a newer one is published.\n"
                   + "A couple of kilobytes; nothing is downloaded or changed by asking.\n"
                   + "This also runs by itself each time the plugin loads.");
    }

    /// <summary>
    ///     The one instruction the user has to act on, drawn so it cannot be skimmed past.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Installing a pack changes nothing until the client restarts, and that is not a wart to
    ///         be apologised for in small print: the game reads its text once, seconds into startup,
    ///         and keeps it for the session. Somebody who installs, sees the confirmation and carries
    ///         on playing will conclude the plugin is broken, and will be right to, because from where
    ///         they sit nothing happened.
    ///     </para>
    ///     <para>
    ///         So it gets its own banner, at a larger size, above everything except the headline, and
    ///         it stays there until the restart it is asking for. A line of ordinary text under the
    ///         install button had already proved too easy to miss.
    ///     </para>
    /// </remarks>
    private void DrawRestartBanner()
    {
        if (!this.restartPending)
        {
            return;
        }

        ImGui.Separator();
        ImGui.SetWindowFontScale(1.25f);

        Icon(FontAwesomeIcon.PowerOff, Amber);
        ImGui.TextWrapped("RESTART THE CLIENT");
        ImGui.PopStyleColor();

        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped(
            "The new language pack is installed but the game will not read it until it starts again — "
            + "it loads all of its text once, at startup.");
        ImGui.Separator();
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
            // A real bar, and not grey. The first version put "Installing..." in TextDisabled, which
            // for a fast download nobody saw at all and for a slow one said nothing about whether it
            // was moving — the two cases a person most needs told apart from a hang.
            var p = this.progress;
            ImGui.PushStyleColor(ImGuiCol.Text, Green);
            ImGui.TextUnformatted(p.Detail.Length > 0 ? $"{p.Label} — {p.Detail}" : $"{p.Label}...");
            ImGui.PopStyleColor();

            ImGui.ProgressBar(
                p.Fraction ?? -1f * (float)ImGui.GetTime(),
                new Vector2(-1, 6f * ImGuiHelpers.GlobalScale),
                string.Empty);
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
        this.progress = InstallProgress.Working("Starting");

        // Assigned from the worker and read from the draw thread a frame later. A struct field is
        // written atomically enough for that: the worst case is one frame of a slightly stale number,
        // which is invisible next to a bar that redraws sixty times a second.
        var report = new Progress<InstallProgress>(p => this.progress = p);

        _ = Task.Run(async () =>
        {
            var result = await this.installer.InstallAsync(source, report).ConfigureAwait(false);

            if (result.Success)
            {
                this.config.LanguagePackPath = result.Path;
                this.config.ServeLanguagePack = true;
                this.save(this.config);

                var pack = result.Manifest!;
                this.installFailed = false;
                this.installMessage = $"Installed {pack.DisplayName} ({pack.TranslationVersion ?? "unversioned"}).";
                this.restartPending = true;

                // Lets the plugin drop what it learned about the previous pack's update address, and
                // say so in chat where somebody who has closed this window will still see it.
                this.onPackInstalled();
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






