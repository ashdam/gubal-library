using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

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

    /// <summary>The information markers. One colour, because they all mean the same thing.</summary>
    private static readonly Vector4 Blue = new(0.45f, 0.72f, 1f, 1f);

    /// <summary>
    ///     How wide a tooltip picture is drawn, before the interface scale is applied.
    /// </summary>
    /// <remarks>
    ///     The width the screenshots are cut to, so at 100% they are drawn pixel for pixel. The text
    ///     above them wraps at the same width: a narrow column of words over a wide picture reads as
    ///     two things that landed in the same box by accident.
    /// </remarks>
    private const float PictureWidth = 560f;

    private readonly Configuration config;
    private readonly FileDialogManager fileDialogs;
    private readonly Action<Configuration> save;
    private readonly Func<PageStatus> pageStatus;
    private readonly PackInstaller installer;

    /// <summary>What the installed pack holds, whether or not it is being served.</summary>
    /// <remarks>
    ///     A delegate rather than a value, because the pack can be replaced while this window is open.
    ///     The plugin caches behind it; this is called every frame and must never enumerate.
    /// </remarks>
    private readonly Func<PackContents> contents;

    /// <summary>Loads the before-and-after pictures shipped inside this assembly.</summary>
    /// <remarks>
    ///     Dalamud keeps the decoded texture behind this, so asking for the same resource on every
    ///     frame is the intended use and not a leak. Nothing is cached here.
    /// </remarks>
    private readonly ITextureProvider textures;

    private readonly Action onPackInstalled;

    /// <summary>Asks the plugin to put the update question again. Answered a second or so later.</summary>
    /// <remarks>
    ///     The plugin's job rather than this window's, because the manifest to ask about depends on
    ///     what is being served and what is merely installed — which is a fact the window has no
    ///     business learning to compute a second time.
    /// </remarks>
    private readonly Action checkForUpdate;

    /// <summary>Turns the startup update on or off, and Dalamud's boot wait with it.</summary>
    /// <remarks>
    ///     Not a plain field write like every other setting in this window, because ticking it also
    ///     asks Dalamud to hold the game's boot, and that setting is neither this window's nor this
    ///     plugin's. Unticking asks for nothing back.
    /// </remarks>
    private readonly Action<bool> setAutoUpdate;

    /// <summary>Whether Dalamud holds the game's start for its plugins. Null when it cannot be told.</summary>
    private readonly Func<bool?> dalamudWaits;

    private readonly Action openDalamudSettings;

    private volatile bool installing;
    private InstallProgress progress;
    private string? installMessage;
    private bool installFailed;

    /// <summary>
    ///     Why a restart is owed, or null when none is. Never cleared once set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only a restart clears it, because only a restart acts on it: the client reads its text
    ///         once at startup, so between changing something and restarting the game is still showing
    ///         what it read and there is nothing the plugin can do about that.
    ///     </para>
    ///     <para>
    ///         A reason rather than a flag, because two different things now owe one and the sentence
    ///         under the banner has to say which. "The new language pack is installed" in front of
    ///         somebody who only unticked a checkbox describes something that did not happen.
    ///     </para>
    /// </remarks>
    private string? restartReason;

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
        Func<PackContents> contents,
        ITextureProvider textures,
        PackInstaller installer,
        Action onPackInstalled,
        Action checkForUpdate,
        Action<bool> setAutoUpdate,
        Func<bool?> dalamudWaits,
        Action openDalamudSettings,
        string version)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.config = config;
        this.save = save;
        this.fileDialogs = fileDialogs;
        this.pageStatus = pageStatus;
        this.contents = contents;
        this.textures = textures;
        this.installer = installer;
        this.onPackInstalled = onPackInstalled;
        this.checkForUpdate = checkForUpdate;
        this.setAutoUpdate = setAutoUpdate;
        this.dalamudWaits = dalamudWaits;
        this.openDalamudSettings = openDalamudSettings;

        this.SizeConstraints = new WindowSizeConstraints
        {
            // Wide enough for the longest part name in the Translated parts tab, indented under its
            // group. The old minimum predates that tab and clipped the labels it is made of, which
            // for a list whose whole job is to be readable is the one thing it cannot do.
            MinimumSize = new Vector2(560, 220),
            MaximumSize = new Vector2(900, 800),
        };
    }

    /// <summary>
    ///     The status line and the restart banner, then the tabs.
    /// </summary>
    /// <remarks>
    ///     Those two stay above the tab bar because neither is about a tab: one answers "is another
    ///     language actually reaching the game" and the other "do I have to restart", and both remain
    ///     true whichever tab happens to be open. Hiding the restart banner behind the tab somebody is
    ///     not looking at would undo the whole reason it is a banner.
    /// </remarks>
    public override void Draw()
    {
        var pages = this.pageStatus();
        var changed = false;

        this.DrawHeadline(pages);
        this.DrawRestartBanner();
        ImGui.Spacing();

        using (var bar = ImRaii.TabBar("##gubalTabs"))
        {
            if (bar)
            {
                using (var tab = ImRaii.TabItem("Language pack"))
                {
                    if (tab)
                    {
                        this.DrawSetupTab(pages, ref changed);
                    }
                }

                using (var tab = ImRaii.TabItem("Translated parts"))
                {
                    if (tab)
                    {
                        this.DrawPartsTab(ref changed);
                    }
                }
            }
        }

        if (changed)
        {
            this.save(this.config);
        }
    }

    /// <summary>Where the pack comes from and how it keeps itself current. All of the old window.</summary>
    private void DrawSetupTab(PageStatus pages, ref bool changed)
    {
        // Suppressed once something has been installed, because everything it could say is about the
        // pack that is on its way out. Whether the OLD pack has a newer version published stopped
        // being anybody's problem the moment a new one was put in its place.
        if (this.restartReason is null)
        {
            this.DrawUpdateNotice(pages);
        }

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
    }

    /// <summary>
    ///     Which parts of the translation are served, and what switching one off means.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nothing in this list carries a number.</b> The obvious one to show is how many pages
    ///         each part holds, and it is worse than useless: the quest text is three thousand tiny
    ///         files and the whole interface is one, so the honest-looking figures say the interface is
    ///         a rounding error when it is thirteen thousand lines of it. Counting sheets says the same
    ///         thing less clearly. Until a pack can state how much text a part actually holds, the only
    ///         count on screen is of the checkboxes themselves, which is a fact about this window and
    ///         cannot be misread as a fact about the game.
    ///     </para>
    ///     <para>
    ///         Drawn from the installed pack rather than from the table, so a part the pack does not
    ///         hold is never offered — and, the other way round, text this build has no name for is
    ///         still listed rather than quietly served with no way to refuse it.
    ///     </para>
    /// </remarks>
    private void DrawPartsTab(ref bool changed)
    {
        var pack = this.contents();

        if (pack.Layout.Count == 0)
        {
            ImGui.TextWrapped(
                "Nothing to list yet. Install a language pack on the other tab and its parts appear here.");
            return;
        }

        // What the tab is for, said once at the top. Every box below explains itself on hover, but a
        // list of fourteen checkboxes with no opening line leaves the reader to work out from the
        // names alone whether ticking one adds a translation or removes it.
        ImGui.TextWrapped(
            "Choose how much of the game this language pack translates. Each box below is one part of "
            + "the game's text: untick it and that part comes back in the language the game shipped "
            + "with, while everything still ticked stays translated. Hover a box to see what it "
            + "covers.");
        ImGui.Spacing();

        // Body text, not a tooltip. Every setting in this plugin waits for the next start, and small
        // print saying so has already been proved too easy to miss once.
        ImGui.TextDisabled("Changes here take effect when the client next starts.");
        ImGui.Spacing();

        var total = pack.PartCount;
        var off = pack.PartsOff(this.config.DisabledSheets).Count;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{total - off} of {total} on");

        // One button, doing the only bulk thing worth offering. There is deliberately no "turn
        // everything off": that is the "use this language pack" switch on the other tab, and a second
        // control for the same fact is a control that can disagree with the first.
        const string reset = "Turn everything on";
        var width = ImGui.CalcTextSize(reset).X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width);

        using (ImRaii.Disabled(off == 0))
        {
            if (ImGui.Button(reset))
            {
                this.config.DisabledSheets.Clear();
                this.NoteParted();
                changed = true;
            }
        }

        ImGui.Separator();

        using var scroll = ImRaii.Child("##parts");
        if (!scroll)
        {
            return;
        }

        foreach (var group in pack.Layout)
        {
            this.DrawPartGroup(group, ref changed);
        }
    }

    /// <summary>
    ///     A heading with a checkbox that speaks for everything under it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>ImGui has no three-state checkbox, so this one answers a single question</b> — is
    ///         <em>all</em> of this group on? — and when the answer is no, an amber count says how much
    ///         of it is. An empty box beside a group that is half served would otherwise be a plain
    ///         lie about what is below it. Clicking a partial group turns all of it on; clicking a
    ///         full one turns all of it off.
    ///     </para>
    ///     <para>
    ///         A group holding one part is drawn as that part. An expander whose contents are exactly
    ///         what its heading already said is a click that buys nothing.
    ///     </para>
    /// </remarks>
    private void DrawPartGroup(GroupView group, ref bool changed)
    {
        if (group.Parts.Length == 1)
        {
            var only = group.Parts[0];

            // Labelled with the part's own name and not the group's, which is the same words for a
            // group that only ever held one and the honest ones for a group this pack has cut down to
            // one. Calling a lone "title screen" checkbox "Menus and interface" would promise the rest
            // of the group, and unticking it would then look like it had failed.
            this.DrawPart(only, only.Part.Name, group.Warning, only.Part.Image ?? group.Image, ref changed);
            return;
        }

        var off = group.Parts.Count(p => p.Sheets.Any(this.config.DisabledSheets.Contains));
        var all = off == 0;

        if (ImGui.Checkbox($"##group_{group.Name}", ref all))
        {
            this.SetSheets(group.Parts.SelectMany(p => p.Sheets), all);
            changed = true;
        }

        ImGui.SameLine();

        using var node = ImRaii.TreeNode(group.Name);

        // On the heading itself as well as on the marker beside it. Hovering the words is what
        // people do; hanging everything off a small icon left two groups with no explanation at
        // all — and, because the picture rides along with the tooltip, no picture either.
        var explain = ExplainGroup(group);
        this.Tip(explain, group.Image);

        if (off > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Amber, $"· {group.Parts.Length - off} of {group.Parts.Length} on");
        }

        // Every group gets one. A row with no marker at all reads as a row with nothing to say,
        // which was wrong for four of the six.
        this.Marker(explain, group.Image);

        if (!node)
        {
            return;
        }

        foreach (var part in group.Parts)
        {
            // The group's own picture stays on the group's marker rather than being repeated on
            // every row underneath it. A part that has one of its own still shows it: the pair for
            // the Duty Finder is a photograph of that window and says nothing about the retainer
            // bell or the title screen sharing its group.
            this.DrawPart(part, part.Part.Name, part.Part.Warning, part.Part.Image, ref changed);
        }
    }

    /// <summary>One checkbox, named for what the player sees rather than for the file behind it.</summary>
    /// <remarks>
    ///     The sheet names go in the tooltip and nowhere else. They are the right answer to "which of
    ///     these is misbehaving" and the wrong answer to "what am I switching off", and only the
    ///     second question is being asked at the moment somebody reads the label.
    /// </remarks>
    private void DrawPart(PartView view, string label, string? warning, string? image, ref bool changed)
    {
        // Ticked only when none of it is off. A part can cover more than one sheet, and a saved
        // choice from a build that split them differently can leave half of one switched off; a tick
        // beside that would promise a translation the user is not getting. Clicking turns all of it
        // back on, which is also how the mixed state gets tidied away.
        var on = !view.Sheets.Any(this.config.DisabledSheets.Contains);

        if (ImGui.Checkbox($"{label}##part_{string.Join('_', view.Sheets)}", ref on))
        {
            this.SetSheets(view.Sheets, on);
            changed = true;
        }

        var explain = Explain(view, warning);
        this.Tip(explain, image);
        this.Marker(explain, image);
    }

    /// <summary>
    ///     Where this is on screen, then anything worth thinking about before switching it off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Where it is comes first, because that is the only question being asked.</b> Somebody
    ///         reading this is deciding whether they want a thing translated, and to decide that they
    ///         have to recognise the thing.
    ///     </para>
    ///     <para>
    ///         It used to lead with a sentence explaining that a part switched off is served from the
    ///         game's own files rather than blanked. That is how the plugin works and not what it
    ///         does: from where the player sits nothing else was ever going to happen, so it filled
    ///         the first line of every tooltip with an answer to a question nobody had.
    ///     </para>
    ///     <para>
    ///         <b>The sheet names come last, and they are back.</b> They were dropped once as noise —
    ///         the right answer to "which file is wrong" and the wrong one to "what am I switching
    ///         off". That reasoning held while the boxes matched what a player sees; it stopped
    ///         holding the day two of them were caught promising the wrong thing, because a reader who
    ///         can see <c>logmessage</c> under a box can tell it is not where the speech balloons are.
    ///         They are the only part of this tooltip that cannot drift from what is served.
    ///     </para>
    /// </remarks>
    /// <param name="groupWarning">The group's caveat, when a group has collapsed into this one part.</param>
    private static string Explain(PartView view, string? groupWarning)
    {
        var text = view.Part.Description;

        if (view.Part.Warning is { Length: > 0 } own)
        {
            text += "\n\n" + own;
        }

        if (groupWarning is { Length: > 0 } group)
        {
            text += "\n\n" + group;
        }

        // Only the sheets this pack actually holds, which is what PartView carries — naming one the
        // pack does not have would send somebody looking for text that is not being served.
        return text + "\n\nSheets: " + string.Join(", ", view.Sheets);
    }

    /// <summary>An amber "!" that carries the same words as the control beside it.</summary>
    /// <remarks>
    ///     Repeating the tooltip rather than splitting it: the marker is what draws the eye, so it has
    ///     to be the thing that answers when hovered. A marker that only says "there is something to
    ///     know here" spends a click and tells nobody anything.
    /// </remarks>
    private void Marker(string tooltip, string? image)
    {
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Blue);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
        }

        ImGui.PopStyleColor();
        this.Tip(tooltip, image);
    }

    /// <summary>What a group's tooltip says: its caveat if it has one, then what is inside it.</summary>
    /// <remarks>
    ///     Naming the parts matters more than it looks. "Duty descriptions" is not a phrase anybody
    ///     has met before; "Duty Finder descriptions, Guildhest briefings, Gold Saucer" is three
    ///     things they have seen on screen.
    /// </remarks>
    private static string ExplainGroup(GroupView group)
    {
        var text = group.Description;

        if (group.Warning is { Length: > 0 } warning)
        {
            text += "\n\n" + warning;
        }

        return text + "\n\nIn this group: " + string.Join(", ", group.Parts.Select(p => p.Part.Name)) + ".";
    }

    /// <summary>
    ///     A tooltip that can carry a picture of what the setting does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The picture is a pair of screenshots, the same window with the part switched off and
    ///         switched on. It answers the question the words cannot: what "the interface" or "the
    ///         combat log" is, for somebody who has never had to name those separately.
    ///     </para>
    ///     <para>
    ///         <b>Absence is normal.</b> A group without one, or a build where the resource failed to
    ///         load, draws the text and nothing else. These are photographs of one language pack in
    ///         one patch of the game; there will always be groups nobody has taken a pair for.
    ///     </para>
    /// </remarks>
    private void Tip(string text, string? image)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(PictureWidth * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();

        if (image is { Length: > 0 })
        {
            this.DrawComparison(image);
        }

        ImGui.EndTooltip();
    }

    /// <summary>
    ///     The same window with the part switched off and switched on, one above the other.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two pictures and two captions, rather than one picture with the captions inside
    ///         it.</b> The words belong to the interface: rewording them, recolouring them or one day
    ///         translating them should not mean finding the script that cut the screenshots and
    ///         producing the whole set again.
    ///     </para>
    ///     <para>
    ///         Either half may be missing and the other is still worth drawing — a pair is taken by
    ///         hand, and the two halves of one are taken on different days.
    ///     </para>
    /// </remarks>
    private void DrawComparison(string name)
    {
        var off = this.Picture($"{name}-off");
        var on = this.Picture($"{name}-on");

        if (off is null && on is null)
        {
            return;
        }

        // SIDE BY SIDE WHEN BOTH ARE THERE, because that is the reading order of the thing being
        // shown: English on the left, Spanish on the right, the same window twice. Stacked, the eye
        // has to scroll between them and the comparison stops being one glance.
        //
        // HALF THE WIDTH EACH SO THE PAIR STILL FITS THE TOOLTIP. A pair drawn at full width would
        // make the tooltip twice as wide as the text above it, which no screen thanks you for.
        // A LONE HALF KEEPS THE FULL WIDTH: it is not competing with anything.
        var both = off is not null && on is not null;
        var width = (both ? PictureWidth / 2f : PictureWidth) * ImGuiHelpers.GlobalScale;

        ImGui.Spacing();

        if (off is not null)
        {
            this.Half("Switched off", Amber, off, width);
        }

        if (both)
        {
            ImGui.SameLine();
        }

        if (on is not null)
        {
            this.Half("Switched on", Green, on, width);
        }
    }

    /// <summary>One captioned picture, as a group so that <c>SameLine</c> puts the next one beside it.</summary>
    private void Half(string caption, Vector4 colour, IDalamudTextureWrap wrap, float width)
    {
        ImGui.BeginGroup();
        ImGui.TextColored(colour, caption);
        ImGui.Image(wrap.Handle, new Vector2(width, wrap.Height * (width / wrap.Width)));
        ImGui.EndGroup();
    }

    /// <summary>The picture for a group, or null while it loads or if it is not there.</summary>
    private IDalamudTextureWrap? Picture(string name)
    {
        var resource = $"GubalLibrary.images.tooltips.{name}.png";
        return this.textures.GetFromManifestResource(typeof(Plugin).Assembly, resource)
            .TryGetWrap(out var wrap, out _)
            ? wrap
            : null;
    }

    /// <summary>Switches a run of sheets on or off together, and notes that a restart is owed.</summary>
    private void SetSheets(IEnumerable<string> sheets, bool on)
    {
        foreach (var sheet in sheets)
        {
            if (on)
            {
                this.config.DisabledSheets.Remove(sheet);
            }
            else
            {
                this.config.DisabledSheets.Add(sheet);
            }
        }

        this.NoteParted();
    }

    /// <summary>
    ///     Raises the restart banner for a change of parts, without talking over an install.
    /// </summary>
    /// <remarks>
    ///     An install already owes a restart and says something more urgent about it, so it keeps the
    ///     banner it set. Both end in the same instruction; only the sentence above it differs.
    /// </remarks>
    private void NoteParted()
    {
        this.restartReason ??=
            "You changed which parts are translated. The game reads all of its text once, at "
            + "startup, so this takes effect the next time it starts.";
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
        if (this.restartReason is not { Length: > 0 } reason)
        {
            return;
        }

        ImGui.Separator();
        ImGui.SetWindowFontScale(1.25f);

        Icon(FontAwesomeIcon.PowerOff, Amber);
        ImGui.TextWrapped("RESTART THE CLIENT");
        ImGui.PopStyleColor();

        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped(reason);
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

        this.DrawAutoUpdateRow();

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
    ///     The switch that makes an update arrive by itself, and the conditions it depends on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Beside "use this language pack" rather than beside the update notice above, because it
    ///         is the same kind of thing: a standing decision about the pack, not a reaction to what a
    ///         check happened to find. It also has to stay on screen while a restart is pending, and
    ///         everything in that notice is deliberately hidden then.
    ///     </para>
    ///     <para>
    ///         <b>Both reasons it can be unavailable are written out rather than left to a tooltip.</b>
    ///         A greyed box with no explanation is the same as a broken one, and the two cases have
    ///         different answers: a pack installed from a file has no address to fetch a newer one
    ///         from, while Dalamud not waiting for its plugins is a setting away from working — so
    ///         that one gets a button rather than a sentence.
    ///     </para>
    /// </remarks>
    private void DrawAutoUpdateRow()
    {
        // The same three preconditions the startup path checks, and for the same reasons: a folder
        // the user pointed at is theirs to manage, and a local file would be re-unpacked on every
        // boot for ever, since the version on disk would never move.
        var available = this.config.LanguagePackPath.Length > 0
                        && PackInstaller.IsRemote(this.config.PackSource)
                        && string.Equals(
                            this.config.LanguagePackPath,
                            this.installer.InstalledPath,
                            StringComparison.OrdinalIgnoreCase);

        var auto = this.config.AutoUpdatePack;
        using (ImRaii.Disabled(!available))
        {
            if (ImGui.Checkbox("Fetch a newer pack while the game starts", ref auto))
            {
                // Saves itself: it writes Dalamud's configuration as well as this one.
                this.setAutoUpdate(auto);
            }
        }

        SetTooltip("Checks at every start, and if a newer pack is published, downloads and installs it\n"
                   + "before the game reads its text — so the new translation is live in that session\n"
                   + "and nothing has to be restarted.\n\n"
                   + "The game's start is held while it downloads. Ticking this also turns on Dalamud's\n"
                   + "\"wait for plugins\", which is what makes holding it possible.");

        ImGui.Indent();

        if (!available)
        {
            ImGui.TextDisabled(
                "Available for a pack installed from a link. A pack taken from a file or used where it "
                + "lies has no address to ask.");
        }
        else if (auto)
        {
            this.DrawBootWaitState();
        }

        ImGui.Unindent();
    }

    /// <summary>Says whether Dalamud will actually hold the game's start, since everything rests on it.</summary>
    /// <remarks>
    ///     Ticking the box sets it, so this is normally a line of reassurance. It earns its place in
    ///     the two cases where it is not: the user turned it off again afterwards, or this build of
    ///     Dalamud keeps the setting somewhere this plugin can no longer reach — in which case the
    ///     honest thing is to say so and point at the window that can.
    /// </remarks>
    private void DrawBootWaitState()
    {
        if (this.dalamudWaits() is true)
        {
            ImGui.TextDisabled("Dalamud will hold the game's start until the update has finished.");
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGui.TextWrapped(
            "Dalamud is not set to wait for plugins before the game loads, so nothing will be fetched "
            + "at startup: the game would read its text while the download was still running. Updates "
            + "are offered above instead.");
        ImGui.PopStyleColor();

        if (ImGui.Button("Open Dalamud settings##bootWait"))
        {
            this.openDalamudSettings();
        }

        SetTooltip("It is on the General tab, called \"Wait for plugins before game loads\".");
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

                // Overwrites whatever a part change had set. Both owe the same restart; this is the
                // more urgent thing to say about it.
                this.restartReason =
                    "The new language pack is installed but the game will not read it until it starts "
                    + "again — it loads all of its text once, at startup.";

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

    /// <summary>
    ///     A tooltip that wraps, which is not what ImGui does by itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Left to itself a tooltip is one line per paragraph, however long the paragraph is. The
    ///         tooltips already here were written around that, each line broken by hand; the ones
    ///         explaining what switching a part off costs you run to a paragraph each and came out as
    ///         a band of text wider than the game window, over the top of everything.
    ///     </para>
    ///     <para>
    ///         Wrapping here rather than at each call site, so the hand-broken ones keep their breaks
    ///         and the long ones stop needing any.
    ///     </para>
    /// </remarks>
    private static void SetTooltip(string text)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(400f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
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






