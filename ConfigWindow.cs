using System.Numerics;
using CheapLoc;
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
///     Ordered by what a new user has to do, not by what the plugin does internally: it ships no
///     translations, so a fresh install can do exactly one useful thing — be pointed at a folder.
/// </remarks>
internal sealed class ConfigWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>The information markers. One colour, because they all mean the same thing.</summary>
    private static readonly Vector4 Blue = new(0.45f, 0.72f, 1f, 1f);

    /// <summary>How wide a tooltip picture is drawn, before the interface scale is applied.</summary>
    /// <remarks>
    ///     The width the screenshots are cut to, so at 100% they are pixel for pixel. The text above
    ///     wraps at the same width, or the two read as things that landed in one box by accident.
    /// </remarks>
    private const float PictureWidth = 760f;

    /// <summary>Wider than this and a picture is drawn above its partner rather than beside it.</summary>
    /// <remarks>
    ///     Where halving a screenshot stops being a smaller picture and starts being an unreadable
    ///     one. Set by the shapes in hand: cursor crops are 1.2 and survive it, the help windows are
    ///     2.4 and did not — drawn at 283 from 560, their text went to nothing.
    /// </remarks>
    private const float PanoramicRatio = 2.2f;

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

    /// <summary>Why a restart is owed, or null when none is. Never cleared once set.</summary>
    /// <remarks>
    ///     Only a restart acts on it: the client reads its text once at startup. A reason rather than
    ///     a flag, because two different things owe one — "the new language pack is installed" in
    ///     front of somebody who only unticked a checkbox describes something that did not happen.
    /// </remarks>
    private string? restartReason;

    /// <summary>Whether the pack reached Dalamud, or null when none was asked for.</summary>
    private readonly Func<ShadowState?> shadowState;

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
        Func<ShadowState?> shadowState,
        string version)
        : base($"Gubal Library ({version})###GubalLibraryConfig")
    {
        this.shadowState = shadowState;
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
    ///     The restart banner, then the tabs.
    /// </summary>
    /// <remarks>
    ///     The banner stays above the tab bar because it is not about a tab. <b>Nothing else belongs
    ///     up here</b>, and a status headline in particular does not: it would repeat the pack and
    ///     version already shown inside the tab, and any warning colour keyed on "no read answered
    ///     yet" fires on the ordinary state after every hot reload. The reads counter is a number in
    ///     the pack block rather than an alarm, for that reason.
    /// </remarks>
    public override void Draw()
    {
        var pages = this.pageStatus();
        var changed = false;

        this.DrawRestartBanner();
        ImGui.Spacing();

        using (var bar = ImRaii.TabBar("##gubalTabs"))
        {
            if (bar)
            {
                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Pack", "Language pack")))
                {
                    if (tab)
                    {
                        this.DrawSetupTab(pages, ref changed);
                    }
                }

                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Parts", "Translated parts")))
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
        // The installed pack first: what is loaded, what it holds, and — on the same line as its
        // name — the one button that can say something new about it. Where the pack CAME from is a
        // separate question and sits below, because it is answered once and then never again.
        this.DrawInstalledPack(pages);

        ImGui.Separator();
        this.DrawLanguagePackRow(ref changed);

        ImGui.Separator();
        this.DrawDiagnostics(ref changed);
    }

    /// <summary>
    ///     Which parts of the translation are served, and what switching one off means.
    /// </summary>
    /// <remarks>
    ///     <b>Nothing in this list carries a number.</b> Pages per part would be worse than useless —
    ///     the quest text is three thousand tiny files and the whole interface is one, so the figures
    ///     call the interface a rounding error when it is thirteen thousand lines. The only count on
    ///     screen is of the checkboxes, which cannot be misread as a fact about the game. Drawn from
    ///     the installed pack rather than the table, so a part the pack lacks is never offered and
    ///     text this build cannot name is still listed rather than served with no way to refuse it.
    /// </remarks>
    private void DrawPartsTab(ref bool changed)
    {
        var pack = this.contents();

        if (pack.Layout.Count == 0)
        {
            ImGui.TextWrapped(Loc.Localize("Parts.Empty",
                "Nothing to list yet. Install a language pack on the other tab and its parts appear here."));
            return;
        }

        // What the tab is for, said once at the top. Every box below explains itself on hover, but a
        // list of fourteen checkboxes with no opening line leaves the reader to work out from the
        // names alone whether ticking one adds a translation or removes it.
        ImGui.TextWrapped(Loc.Localize("Parts.Intro",
            "Choose how much of the game this language pack translates. Each box below is one part of "
            + "the game's text: untick it and that part comes back in the language the game shipped "
            + "with, while everything still ticked stays translated. Hover a box to see what it "
            + "covers."));
        ImGui.Spacing();

        // Body text, not a tooltip. Every setting in this plugin waits for the next start, and small
        // print saying so has already been proved too easy to miss once.
        ImGui.TextDisabled(Loc.Localize("Parts.NextStart",
            "Changes here take effect when the client next starts."));
        ImGui.Spacing();

        var total = pack.PartCount;
        var off = pack.PartsOff(this.config.DisabledSheets).Count;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.Format(Loc.Localize("Parts.Count", "{0} of {1} on"), total - off, total));

        // One button, doing the only bulk thing worth offering. There is deliberately no "turn
        // everything off": that is the "use this language pack" switch on the other tab, and a second
        // control for the same fact is a control that can disagree with the first.
        var reset = Loc.Localize("Parts.TurnAllOn", "Turn everything on");
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
    ///     <b>ImGui has no three-state checkbox</b>, so this one answers "is <em>all</em> of this
    ///     group on?" and an amber count says how much of it is when the answer is no. Clicking a
    ///     partial group turns all of it on, a full one off. A group holding one part is drawn as
    ///     that part: an expander whose contents restate its heading is a click that buys nothing.
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
            ImGui.TextColored(Amber, "· " + string.Format(
                Loc.Localize("Parts.Count", "{0} of {1} on"), group.Parts.Length - off, group.Parts.Length));
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
        this.Tip(explain, image, view.Sheets);
        this.Marker(explain, image, view.Sheets);
    }

    /// <summary>
    ///     The sheet names, under a rule and set in the monospaced face.
    /// </summary>
    /// <remarks>
    ///     <b>Only the sheets this pack actually holds</b>, which is what <see cref="PartView" />
    ///     carries: naming one it lacks sends somebody looking for text that is not served.
    ///     <b>Monospaced rather than bold</b>, and not as a compromise — Dalamud ships no bold face,
    ///     and mono says the right thing anyway, since these are identifiers and not words.
    /// </remarks>
    private void Sheets(string[] sheets)
    {
        if (sheets.Length == 0)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(Grey, "Sheets");

        using (ImRaii.PushFont(UiBuilder.MonoFont))
        {
            ImGui.PushTextWrapPos(PictureWidth * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Join(", ", sheets));
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    ///     Where this is on screen, then anything worth thinking about before switching it off.
    /// </summary>
    /// <remarks>
    ///     <b>Where it is comes first</b>, because that is the only question being asked: to decide
    ///     whether they want a thing translated, somebody has to recognise the thing. <b>The sheet
    ///     names come last, and they are back</b> after being dropped once as noise — they stopped
    ///     being noise the day two boxes were caught promising the wrong thing, since a reader who
    ///     sees <c>logmessage</c> can tell it is not where the speech balloons are. They are the only
    ///     part of the tooltip that cannot drift from what is served.
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

        return text;
    }

    /// <summary>An amber "!" that carries the same words as the control beside it.</summary>
    /// <remarks>
    ///     Repeating the tooltip rather than splitting it: the marker is what draws the eye, so it has
    ///     to be the thing that answers when hovered. A marker that only says "there is something to
    ///     know here" spends a click and tells nobody anything.
    /// </remarks>
    private void Marker(string tooltip, string? image, string[]? sheets = null)
    {
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Blue);
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
        }

        ImGui.PopStyleColor();
        this.Tip(tooltip, image, sheets);
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
    ///     A pair of screenshots, the same window with the part off and on, answering what the words
    ///     cannot: what "the interface" is for somebody who never had to name it. <b>Absence is
    ///     normal</b> — these are photographs of one pack in one patch, and a group without one draws
    ///     the text and nothing else.
    /// </remarks>
    /// <param name="sheets">The footnote: which file this is, for somebody who has read the rest.</param>
    private void Tip(string text, string? image, string[]? sheets = null)
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

        if (sheets is not null)
        {
            this.Sheets(sheets);
        }

        ImGui.EndTooltip();
    }

    /// <summary>
    ///     The same window with the part switched off and switched on, one above the other.
    /// </summary>
    /// <remarks>
    ///     <b>Two pictures and two captions, not one picture with the captions inside it.</b> The
    ///     words belong to the interface: rewording or translating them should not mean cutting the
    ///     whole set of screenshots again. Either half may be missing and the other is still worth
    ///     drawing — a pair is taken by hand, and its halves on different days.
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
        ImGui.Spacing();

        var available = PictureWidth * ImGuiHelpers.GlobalScale;

        if (off is null || on is null)
        {
            // A LONE HALF KEEPS THE FULL WIDTH: it is not competing with anything, and half a pair
            // is normal — the two halves of one are taken on different days.
            var only = off ?? on!;
            this.Half(
                off is null ? Loc.Localize("Parts.On", "Switched on") : Loc.Localize("Parts.Off", "Switched off"),
                off is null ? Green : Amber,
                only, available, only.Height * (available / only.Width));
            return;
        }

        var offRatio = (float)off.Width / off.Height;
        var onRatio = (float)on.Width / on.Height;

        // A PANORAMIC PAIR STACKS INSTEAD, because side by side it is unreadable. `examine` is a
        // 1041x259 crop of a message window: halved it draws at 274x68 and the two lines of text
        // inside it are four pixels tall. Stacked, each keeps the full width and twice the height.
        //
        // The threshold is per image, not on the pair, so one panoramic half is enough to stack
        // both — a pair drawn two different ways is worse than either way.
        if (offRatio >= PanoramicRatio || onRatio >= PanoramicRatio)
        {
            this.Half(Loc.Localize("Parts.Off", "Switched off"), Amber, off, available, available / offRatio);
            this.Half(Loc.Localize("Parts.On", "Switched on"), Green, on, available, available / onRatio);
            return;
        }

        // OTHERWISE THE PAIR SHARES A HEIGHT, NOT A WIDTH, and that is the point of this arithmetic.
        // These are screenshots cropped by hand on different days: `interactable-off` is 618x516 and
        // its partner 551x560. Drawn to a common width the second comes out taller, the captions
        // stop lining up, and the two pictures no longer read as the same window twice.
        //
        // Solve for the height at which both fit: at height H the widths are H*r1 and H*r2, so
        // H = (available - spacing) / (r1 + r2). The pair then fills the width exactly, whatever
        // shape the screenshots happen to be.
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var height = (available - spacing) / (offRatio + onRatio);

        this.Half(Loc.Localize("Parts.Off", "Switched off"), Amber, off, height * offRatio, height);
        ImGui.SameLine();
        this.Half(Loc.Localize("Parts.On", "Switched on"), Green, on, height * onRatio, height);
    }

    /// <summary>One captioned picture, as a group so that <c>SameLine</c> puts the next one beside it.</summary>
    private void Half(string caption, Vector4 colour, IDalamudTextureWrap wrap, float width, float height)
    {
        // NEVER LARGER THAN THE SCREENSHOT ITSELF. Widening the tooltip to 760 would otherwise blow
        // the 560-wide help pair up by a third and trade one kind of unreadable for another: a
        // screenshot has no detail above its own resolution, and upscaled game text goes soft in a
        // way that looks like a rendering fault rather than a big picture.
        var scale = ImGuiHelpers.GlobalScale;
        var cap = Math.Min(1f, wrap.Width * scale / width);

        ImGui.BeginGroup();
        ImGui.TextColored(colour, caption);
        ImGui.Image(wrap.Handle, new Vector2(width * cap, height * cap));
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
            Loc.Localize("Restart.Parts",
                "You changed which parts are translated. The game reads all of its text once, at "
                + "startup, so this takes effect the next time it starts.");
    }

    /// <summary>
    ///     The pack that is loaded, everything it says about itself, and whether anything newer exists.
    /// </summary>
    /// <remarks>
    ///     One block, one subject: the name and version on a line with <em>Check for updates</em> at
    ///     the right of it, so the question and the button that answers it share a line, and the
    ///     verdict underneath. Nothing is claimed until a check comes back — a window that says "up
    ///     to date" without having asked is worse than one that says nothing. Recomputed every frame,
    ///     because the reads count changes while the window is open.
    /// </remarks>
    private void DrawInstalledPack(PageStatus pages)
    {
        if (pages.Manifest is not { } pack)
        {
            // The refusal, when there is one, is the whole story: it is the plugin declining to serve
            // pages it has, and it says why. Otherwise nobody has installed anything yet.
            var (colour, text) = pages.Error is { Length: > 0 } error
                ? (Red, error)

                // Three steps, not four. Do not add "tick the box": the Install button already does
                // that, and an instruction asking for something already done reads as a step that
                // did not take.
                : (Amber, Loc.Localize("Pack.None",
                    "NO LANGUAGE PACK LOADED. This plugin ships no translations, so put a link or a "
                    + "folder below, press Install, and restart the client."));

            Icon(FontAwesomeIcon.ExclamationTriangle, colour);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
            return;
        }

        var version = pack.TranslationVersion ?? Loc.Localize("Pack.Unversioned", "unversioned");

        // The verdict lands on this line rather than under it. A clean check has nothing to add to
        // what the line already says — only that it is now known to be the newest — so it says it in
        // the colour and in three words, and no second line appears saying the same version again.
        var clean = pages.Update.State == UpdateState.UpToDate;

        using (ImRaii.PushColor(ImGuiCol.Text, Green, clean))
        {
            ImGui.TextUnformatted(clean
                ? string.Format(Loc.Localize("Pack.UpToDate", "{0}, up to date: {1}"), pack.DisplayName, version)
                : $"{pack.DisplayName} ({version})");
        }

        this.DrawCheckButton(pages);
        DrawPackDetail(pack, pages);

        // SAID HERE BECAUSE THIS IS WHERE SOMEBODY LOOKS when the game came up untranslated. The pack
        // is withheld from the game as well when Dalamud cannot be given it, so the state to report is
        // "nothing is translated", not "one half of something did not happen".
        if (this.shadowState() is { Ok: false } failed)
        {
            ImGui.Spacing();
            Icon(FontAwesomeIcon.ExclamationTriangle, Red);
            ImGui.TextWrapped(Loc.Localize("Pack.NothingServed", "NOTHING IS TRANSLATED THIS SESSION"));
            ImGui.PopStyleColor();
            ImGui.TextWrapped(failed.Message);
        }

        // Suppressed once something has been installed, because everything it could say is about the
        // pack that is on its way out. Whether the OLD pack has a newer version published stopped
        // being anybody's problem the moment a new one was put in its place.
        if (this.restartReason is null)
        {
            this.DrawUpdateNotice(pages);
        }
    }

    /// <summary>
    ///     <em>Check for updates</em>, at the right-hand end of the line the pack is named on.
    /// </summary>
    /// <remarks>
    ///     Disabled rather than hidden when the pack declares no update address. A button that
    ///     vanishes leaves the reader wondering whether this build has one; a greyed one with a
    ///     sentence on hover answers the question where it was asked.
    /// </remarks>
    private void DrawCheckButton(PageStatus pages)
    {
        var label = Loc.Localize("Update.Check", "Check for updates");

        var width = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine(ImGui.GetContentRegionMax().X - width);

        var declared = pages.Update.State != UpdateState.NotDeclared;
        var busy = this.installing || pages.Update.State == UpdateState.Checking;

        using (ImRaii.Disabled(busy || !declared))
        {
            if (ImGui.Button($"{label}##pack"))
            {
                this.checkForUpdate();
            }
        }

        SetTooltip(declared
            ? Loc.Localize("Update.CheckTip",
                "Asks the address inside the installed pack whether a newer one is published.\n"
                + "A couple of kilobytes; nothing is downloaded or changed by asking.\n"
                + "This also runs by itself each time the plugin loads.")
            : Loc.Localize("Update.NoAddressTip",
                "This language pack carries no update address, so there is nothing to ask."));
    }

    /// <summary>
    ///     Says whether the installed pack can keep itself current, and never acts on the answer.
    /// </summary>
    /// <remarks>
    ///     A newer version is offered, not applied: the check costs two kilobytes and runs by itself,
    ///     while taking it means tens of megabytes and a restart, which is a decision. A pack that
    ///     <em>cannot</em> update is said out loud too — no address declared and an address gone
    ///     quiet differ in blame and not in effect, and either way the translation on screen is the
    ///     last one this person sees unless they go looking. <b>Every state draws something</b>: with
    ///     a button beside it, a press that changes nothing on screen reads as a broken button.
    /// </remarks>
    private void DrawUpdateNotice(PageStatus pages)
    {
        // Nothing to say about updating something that is not there. The block above already says no
        // pack is loaded, and following it with "and it will never update" reads as a second,
        // separate fault.
        if (pages.Manifest is null)
        {
            return;
        }

        switch (pages.Update.State)
        {
            // The enum's default, and so also what is shown for the seconds between asking and being
            // answered — at load, and again every time the button below is pressed.
            case UpdateState.Checking:
                Icon(FontAwesomeIcon.Hourglass, Grey);
                ImGui.TextWrapped(Loc.Localize("Update.Checking",
                    "Checking whether a newer language pack is published..."));
                ImGui.PopStyleColor();
                break;

            case UpdateState.Available when pages.Update.Published is { } update:
                Icon(FontAwesomeIcon.ArrowUp, Amber);
                ImGui.TextWrapped(
                    string.Format(
                        Loc.Localize("Update.Available", "A newer language pack is published: {0}"),
                        update.TranslationVersion)
                    + (update.GameVersion is { Length: > 0 } game
                        ? string.Format(Loc.Localize("Update.BuiltForGame", ", built for game {0}"), game)
                        : string.Empty));
                ImGui.PopStyleColor();

                using (ImRaii.Disabled(this.installing || this.config.PackSource.Trim().Length == 0))
                {
                    // Reinstalls from where this one came from. There is no address in the manifest
                    // to prefer, on purpose: the pack does not repeat a fact the user already
                    // supplied, and successive versions are expected at a stable address.
                    if (ImGui.Button($"{Loc.Localize("Update.Install", "Update")}##pack"))
                    {
                        this.Install(this.config.PackSource);
                    }
                }

                break;

            // No line of its own: a clean check is said by the pack's own name line turning green.
            // See DrawInstalledPack. Two texts for one fact is what this window was just rid of.
            case UpdateState.UpToDate:
                break;

            // Both halves of "this pack will not improve on its own" get said, because from where the
            // player sits the consequence is the same and only the wording should differ.
            // Red, and short. It is a failure of something that was promised, and the reason it
            // failed belongs in the log rather than on screen: the exception text runs to a line and
            // a half of Winsock, which buries the one sentence that tells the reader what to do.
            case UpdateState.Unreachable:
                Icon(FontAwesomeIcon.ExclamationTriangle, Red);
                ImGui.TextWrapped(
                    Loc.Localize("Update.Unreachable",
                        "No connection to the language pack update URL. If it persists, this pack "
                        + "will not update itself, so check where you got it from."));
                ImGui.PopStyleColor();
                break;

            // Amber, not red: nothing is broken. The pack simply never offered to keep itself
            // current, which is a limitation to know about rather than a fault to chase.
            case UpdateState.NotDeclared:
                Icon(FontAwesomeIcon.ExclamationTriangle, Amber);
                ImGui.TextWrapped(
                    Loc.Localize("Update.NotDeclared",
                        "This language pack has no update URL, so it will never update itself."));
                ImGui.PopStyleColor();
                break;
        }
    }

    /// <summary>
    ///     The one instruction the user has to act on, drawn so it cannot be skimmed past.
    /// </summary>
    /// <remarks>
    ///     Installing changes nothing until the client restarts, and that is not a wart to apologise
    ///     for in small print: somebody who installs, sees the confirmation and plays on concludes the
    ///     plugin is broken, and is right to. So it gets a banner, larger, above the tabs, until the
    ///     restart it asks for — a line of ordinary text under the button proved too easy to miss.
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
        ImGui.TextWrapped(Loc.Localize("Restart.Title", "RESTART THE CLIENT"));
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
    ///     <b>"Language pack", never "pages".</b> A page is an <c>.exd</c> file, which is this
    ///     project's vocabulary and not a user's; what somebody installs is a language. A checkbox
    ///     rather than a Serve button, and it says so, because nothing can happen now: the game reads
    ///     its sheets seconds into startup and caches them, so this decides the <em>next</em> start —
    ///     and a Serve button that changed nothing visible read, correctly, as broken.
    /// </remarks>
    private void DrawLanguagePackRow(ref bool changed)
    {
        var source = this.config.PackSource;

        // Aligned to the frame padding, not drawn at the raw cursor: text placed beside an input box
        // sits at the top of it otherwise, a couple of pixels above the text inside the box.
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Loc.Localize("Setup.PathLabel", "Language pack"));
        ImGui.SameLine();

        // Negative width, so the box gives back a fixed strip to the buttons on its right and takes
        // whatever is left of the row. Both ends stay put as the window resizes.
        ImGui.SetNextItemWidth(-150f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##packSource", ref source, 2048))
        {
            this.config.PackSource = source;
            changed = true;
        }

        SetTooltip(Loc.Localize("Setup.PathTip",
            "A .zip, a link to one, or a folder that has already been unpacked.\n"
            + "Whatever it is must contain gubal-manifest.json, which says what the\n"
            + "pack is and which game version it was built for."));

        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Localize("Setup.Browse", "Browse...")}##packSource"))
        {
            this.BrowseForLanguagePack();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(this.installing || this.config.PackSource.Trim().Length == 0))
        {
            if (ImGui.Button($"{Loc.Localize("Setup.Install", "Install")}##packSource"))
            {
                this.Install(this.config.PackSource);
            }
        }

        SetTooltip(Loc.Localize("Setup.InstallTip",
            "Downloads and unpacks it if it needs it, then serves it from the next start.\n"
            + "Nothing is fetched unless you press this."));

        var serve = this.config.ServeLanguagePack;
        using (ImRaii.Disabled(this.config.LanguagePackPath.Length == 0))
        {
            if (ImGui.Checkbox(
                    Loc.Localize("Setup.Serve", "Use this language pack from the next start"), ref serve))
            {
                this.config.ServeLanguagePack = serve;
                changed = true;
            }
        }

        SetTooltip(Loc.Localize("Setup.ServeTip",
            "Gives the game the pack's text instead of its own.\n"
            + "Takes effect when the client next starts: the game reads its text once\n"
            + "at startup and keeps it for the session, so this cannot be switched on mid-game."));

        this.DrawAutoUpdateRow();

        if (this.installing)
        {
            // A real bar, and not grey. The first version put "Installing..." in TextDisabled, which
            // for a fast download nobody saw at all and for a slow one said nothing about whether it
            // was moving — the two cases a person most needs told apart from a hang.
            var p = this.progress;
            ImGui.PushStyleColor(ImGuiCol.Text, Green);
            ImGui.TextUnformatted(p.Detail.Length > 0 ? $"{p.Label}: {p.Detail}" : $"{p.Label}...");
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
    ///     Beside "use this language pack" because it is the same kind of thing — a standing decision
    ///     about the pack, not a reaction to a check — and because it must stay on screen while a
    ///     restart is pending, when the update notice is hidden. <b>Both reasons it can be
    ///     unavailable are written out</b>, since a greyed box with no explanation is a broken one:
    ///     a pack from a file has no address to ask, while Dalamud not waiting for plugins is a
    ///     setting away from working, so that one gets a button rather than a sentence.
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

        // WHAT IS HAPPENING, not what is stored. The two part company: this stays in the
        // configuration after a pack is replaced by a local folder, and the startup path then ignores
        // it because it checks the same three preconditions. Drawing the stored value put a ticked
        // box in front of somebody, greyed so they could not untick it, describing a download that
        // was never going to happen. The setting is kept rather than cleared, so pointing at a link
        // again brings back what they asked for.
        var auto = this.config.AutoUpdatePack && available;
        using (ImRaii.Disabled(!available))
        {
            if (ImGui.Checkbox(
                    Loc.Localize("Setup.Auto", "Fetch a newer pack while the game starts"), ref auto))
            {
                // Saves itself: it writes Dalamud's configuration as well as this one.
                this.setAutoUpdate(auto);
            }
        }

        SetTooltip(Loc.Localize("Setup.AutoTip",
            "Checks at every start, and if a newer pack is published, downloads and installs it\n"
            + "before the game reads its text, so the new translation is live in that session\n"
            + "and nothing has to be restarted.\n\n"
            + "The game's start is held while it downloads. Ticking this also turns on Dalamud's\n"
            + "\"wait for plugins\", which is what makes holding it possible."));

        ImGui.Indent();

        if (!available)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.AutoUnavailable",
                "Available for a pack installed from a link. A pack taken from a file or used where it "
                + "lies has no address to ask."));
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
            ImGui.TextDisabled(Loc.Localize("Boot.Waiting",
                "Dalamud will hold the game's start until the update has finished."));
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGui.TextWrapped(Loc.Localize("Boot.NotWaiting",
            "Dalamud is not set to wait for plugins before the game loads, so nothing will be fetched "
            + "at startup: the game would read its text while the download was still running. Updates "
            + "are offered above instead."));
        ImGui.PopStyleColor();

        if (ImGui.Button($"{Loc.Localize("Boot.OpenSettings", "Open Dalamud settings")}##bootWait"))
        {
            this.openDalamudSettings();
        }

        SetTooltip(Loc.Localize("Boot.WhereTip",
            "It is on the General tab, called \"Wait for plugins before game loads\"."));
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
                this.installMessage = string.Format(
                    Loc.Localize("Install.Done", "Installed {0} ({1})."),
                    pack.DisplayName,
                    pack.TranslationVersion ?? Loc.Localize("Pack.Unversioned", "unversioned"));

                // Overwrites whatever a part change had set. Both owe the same restart; this is the
                // more urgent thing to say about it.
                this.restartReason =
                    Loc.Localize("Restart.Installed",
                        "The new language pack is installed but the game will not read it until it "
                        + "starts again, because it loads all of its text once, at startup.");

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
        var language = pack.LanguageName ?? pack.Language ?? Loc.Localize("Pack.UnknownLanguage", "unknown language");
        ImGui.TextDisabled(pack.Author is { Length: > 0 } author
            ? string.Format(Loc.Localize("Pack.LanguageByAuthor", "{0} by {1}"), language, author)
            : language);

        // Reported as a fraction with its denominator named, not as a bare percentage. The
        // denominator counts only sheets the corpus has opened at all, so a sheet nobody has started
        // on is missing from both sides and the ratio flatters the pack — "of the game" would be a
        // materially different and much smaller number.
        if (pack.Rows > 0)
        {
            ImGui.TextDisabled(string.Format(
                Loc.Localize("Pack.Lines",
                    "{0} of {1} lines translated ({2}) across {3} page(s), in the sheets the pack covers"),
                pack.Lines.ToString("N0"),
                pack.Rows.ToString("N0"),
                pack.TranslatedFraction.ToString("P1"),
                pack.Pages.ToString("N0")));
        }

        ImGui.TextDisabled(string.Format(
            Loc.Localize("Pack.BuiltFor", "Built for game {0}"),
            pack.GameVersion ?? Loc.Localize("Pack.UnknownVersion", "unknown")));

        if (pages.Active)
        {
            ImGui.TextDisabled(string.Format(
                Loc.Localize("Pack.Served", "{0} read(s) answered from disk this session"),
                pages.ServedCount.ToString("N0")));
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
        if (!ImGui.CollapsingHeader(Loc.Localize("Diag.Header", "Diagnostics")))
        {
            return;
        }

        var probe = this.config.ProbeSqPack;
        if (ImGui.Checkbox(Loc.Localize("Diag.Probe", "Log every Excel page the game reads"), ref probe))
        {
            this.config.ProbeSqPack = probe;
            changed = true;
        }

        SetTooltip(Loc.Localize("Diag.ProbeTip",
            "Writes one line per page to /xllog, redirecting nothing.\n"
            + "Attaches at load, so it takes effect on the next client start."));

    }


    /// <summary>
    ///     Picks a <c>.zip</c> or an already-unpacked folder, and fills the source box with it.
    /// </summary>
    /// <remarks>
    ///     A file dialog rather than a folder one, with a filter admitting both: restricting it to
    ///     folders would leave the commonest local case — a freshly downloaded zip — unhelped. Fills
    ///     the box and stops there, because installing on the click would unpack thousands of files
    ///     before the person has read what they picked.
    /// </remarks>
    private void BrowseForLanguagePack()
    {
        var startPath = this.config.LanguagePackPath;
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            startPath = string.Empty;
        }

        this.fileDialogs.OpenFileDialog(
            Loc.Localize("Setup.PickerTitle", "Select a language pack (.zip) or an unpacked folder"),
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
    ///     Left to itself a tooltip is one line per paragraph however long, which turned the ones
    ///     explaining what switching a part off costs into a band wider than the game window.
    ///     Wrapped here rather than at each call site, so the hand-broken ones keep their breaks.
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






