using System.Globalization;
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
using Dalamud.Utility;

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

    /// <summary>Chooser entry for an address that is nobody's published pack: a zip, a folder, a link.</summary>
    private const int OwnPack = -1;

    /// <summary>Chooser entry for a fresh install, where the source box is still empty.</summary>
    private const int NoChoice = -2;

    /// <summary>Chooser entry for the game's own text: a pack installed and deliberately not served.</summary>
    private const int English = -3;

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

    /// <summary>Which language chooser entry is showing. Null re-reads it from the source box.</summary>
    private int? chosenPack;

    /// <summary>The manifest fetched for each published language this session, null when it could not be.</summary>
    private readonly Dictionary<string, PackManifest?> published = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Languages whose fetch has been started, so each is asked for once.</summary>
    private readonly HashSet<string> fetching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The path <see cref="checkedPack" /> describes. Null before anything has been looked at.</summary>
    private string? checkedPath;

    private (PackManifest? Manifest, string? Error) checkedPack;

    /// <summary>The running patch, read once. Empty rather than null once it has been asked for.</summary>
    private string? runningGame;

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

    public override void OnOpen()
    {
        this.chosenPack = null;
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

                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Codex", "Unending Codex")))
                {
                    if (tab)
                    {
                        var enabled = this.config.EnableCodexLinks;
                        if (ImGui.Checkbox(Loc.Localize("Codex.Enable", "Enable Unending Codex links on dialog"), ref enabled))
                        {
                            this.config.EnableCodexLinks = enabled;
                            changed = true;
                        }

                        var picture = this.Picture("codex-on");
                        if (picture != null)
                        {
                            // Crop the preview to the dialogue and its highlighted term.
                            var uv0 = new Vector2(0.295f, 0.777f);
                            var uv1 = new Vector2(0.705f, 0.945f);
                            var crop = new Vector2(picture.Width, picture.Height) * (uv1 - uv0);
                            var width = Math.Min(ImGui.GetContentRegionAvail().X, crop.X);
                            ImGui.Spacing();
                            ImGui.Image(picture.Handle, new Vector2(width, width * crop.Y / crop.X), uv0, uv1);
                        }
                    }
                }

                using (var tab = ImRaii.TabItem(Loc.Localize("Tab.Help", "Help and contact")))
                {
                    if (tab)
                    {
                        DrawHelpTab();
                    }
                }
            }
        }

        if (changed)
        {
            this.save(this.config);
        }
    }

    /// <summary>Which language is installed, and how it keeps itself current.</summary>
    private void DrawSetupTab(PageStatus pages, ref bool changed)
    {
        // The installed pack first: what is loaded, what it holds, and — on the same line as its
        // name — the one button that can say something new about it. Where the pack CAME from is a
        // separate question and sits below, because it is answered once and then never again.
        this.DrawInstalledPack(pages);

        ImGui.Separator();
        this.DrawLanguagePackRow(pages, ref changed);
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
            ImGui.TextWrapped(Loc.Localize("Parts.Empty", "No language pack installed."));
            return;
        }

        // What the tab is for, said once at the top. Every box below explains itself on hover, but a
        // list of fourteen checkboxes with no opening line leaves the reader to work out from the
        // names alone whether ticking one adds a translation or removes it.
        ImGui.TextWrapped(Loc.Localize("Parts.Intro",
            "Untick a part and it goes back to English at the next start. Hover a box for what it covers."));
        ImGui.Spacing();

        // Body text, not a tooltip. Every setting in this plugin waits for the next start, and small
        // print saying so has already been proved too easy to miss once.
        ImGui.TextDisabled(Loc.Localize("Parts.NextStart",
            "Changes here take effect when the game next starts."));
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
        // A group with no text and no picture gets no tooltip and no marker.
        var explain = ExplainGroup(group);
        var tells = explain.Length > 0 || group.Image is not null;

        if (tells)
        {
            this.Tip(explain, group.Image);
        }

        if (off > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Amber, "· " + string.Format(
                Loc.Localize("Parts.Count", "{0} of {1} on"), group.Parts.Length - off, group.Parts.Length));
        }

        if (tells)
        {
            this.Marker(explain, group.Image);
        }

        if (!node)
        {
            return;
        }

        foreach (var part in group.Parts)
        {
            // The group's picture and warning stay on the heading; Explain adds the part's own.
            this.DrawPart(part, part.Part.Name, null, part.Part.Image, ref changed);
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
    private static string Explain(PartView view, string? groupWarning) =>
        Paragraphs(view.Part.Description, view.Part.Warning, groupWarning);

    /// <summary>The texts that are there, a blank line between each. A text left out leaves no gap.</summary>
    private static string Paragraphs(params string?[] texts) =>
        string.Join("\n\n", texts.Where(t => t is { Length: > 0 }));

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

    /// <summary>A group's tooltip: its description, then its warning if it has one.</summary>
    private static string ExplainGroup(GroupView group) => Paragraphs(group.Description, group.Warning);

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

    /// <summary>The flag for a pack's language code, or null while it loads or if there is none.</summary>
    private IDalamudTextureWrap? Flag(string code)
    {
        var resource = $"GubalLibrary.images.flags.{code.ToLowerInvariant()}.png";
        return this.textures.GetFromManifestResource(typeof(Plugin).Assembly, resource)
            .TryGetWrap(out var wrap, out _)
            ? wrap
            : null;
    }

    /// <summary>The height every flag is drawn at; width follows the image so no flag is squashed.</summary>
    private static float FlagHeight => 12f * ImGuiHelpers.GlobalScale;

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
            // Nothing is being served, which is four different situations. A refusal says why itself;
            // the rest differ in whether a pack is installed and whether it was asked for, and
            // telling somebody to install one they already have reads as a step that did not take.
            var (icon, colour, text) = pages.Error is { Length: > 0 } error
                ? (FontAwesomeIcon.ExclamationTriangle, Red, error)
                : this.config.LanguagePackPath.Length == 0
                    ? (FontAwesomeIcon.InfoCircle, Amber, Loc.Localize("Pack.None",
                        "No language pack installed. Choose a language below and press Install."))
                    : this.config.ServeLanguagePack
                        ? (FontAwesomeIcon.Hourglass, Amber, Loc.Localize("Pack.Pending",
                            "A language pack is installed but not in use yet. Restart the game."))
                        : (FontAwesomeIcon.InfoCircle, Grey, Loc.Localize("Pack.Off",
                            "The game is showing its own text. The installed language pack is switched off."));

            Icon(icon, colour);
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

                // No button here: taking it is the chooser's action button, which names the version.
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
        ImGui.TextWrapped(Loc.Localize("Restart.Title", "RESTART THE GAME"));
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
    ///     Which language, where its pack comes from, and the switch that turns it on.
    /// </summary>
    /// <remarks>
    ///     <b>"Language pack", never "pages".</b> A page is an <c>.exd</c> file, which is this
    ///     project's vocabulary and not a user's; what somebody installs is a language. A checkbox
    ///     rather than a Serve button, and it says so, because nothing can happen now: the game reads
    ///     its sheets seconds into startup and caches them, so this decides the <em>next</em> start —
    ///     and a Serve button that changed nothing visible read, correctly, as broken.
    /// </remarks>
    private void DrawLanguagePackRow(PageStatus pages, ref bool changed)
    {
        var chosen = this.DrawLanguageChooser(pages, ref changed);

        if (chosen >= 0 && !KnownPacks.All[chosen].Published)
        {
            DrawUnbuiltLanguage(KnownPacks.All[chosen]);
        }
        else if (chosen == OwnPack)
        {
            this.DrawOwnPack(pages, ref changed);
        }

        if (chosen != OwnPack && chosen != English)
        {
            this.DrawAutoUpdateRow();
        }

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
    ///     The language packs the community publishes, one row each, with what each manifest says.
    ///     Returns which entry is showing.
    /// </summary>
    /// <remarks>Select a language, then use the button below the table.</remarks>
    private int DrawLanguageChooser(PageStatus pages, ref bool changed)
    {
        var chosen = this.Chosen(pages);
        var scale = ImGuiHelpers.GlobalScale;

        using (var table = ImRaii.Table("##languages", 4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX))
        {
            if (!table)
            {
                return chosen;
            }

            ImGui.TableSetupColumn(Loc.Localize("Setup.ColLanguage", "Language"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(Loc.Localize("Setup.ColTranslated", "Translated"), ImGuiTableColumnFlags.WidthFixed, 90f * scale);
            ImGui.TableSetupColumn(Loc.Localize("Setup.ColEnglish", "Kept in English"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.Localize("Setup.ColLinks", "Links"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            for (var i = 0; i < KnownPacks.All.Length; i++)
            {
                var pack = KnownPacks.All[i];
                var (manifest, loading) = pack.Published ? this.PublishedManifest(pack) : (null, false);
                var offline = pack.Published && !loading && manifest is null;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                // A release that cannot be reached cannot be installed either, so its row is not offered.
                if (this.LanguageRow(pack.Code, i == chosen, pack.Code, null, pack.Name, offline))
                {
                    var switching = i != chosen;
                    this.chosenPack = i;
                    chosen = i;
                    this.ServeAgain();

                    if (pack.Published && pack.Source is { } source)
                    {
                        this.config.PackSource = source;

                        // Only a language with a pack behind it, and only with one installed: a
                        // language nobody has built changes nothing, and neither does choosing one
                        // before anything has been installed to replace.
                        if (switching && this.config.LanguagePackPath.Length > 0)
                        {
                            this.NoteServing();
                        }
                    }

                    changed = true;
                }

                DrawCoverageCell(pack, manifest, loading);

                ImGui.TableNextColumn();
                if (manifest?.Coverage is { } coverage)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextWrapped(string.Join(", ", coverage.KeptEnglish));
                }

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (pack.Site is { Length: > 0 } site)
                {
                    IconLink(FontAwesomeIcon.Globe, pack.SiteName ?? site, site);
                    ImGui.SameLine();
                }

                IconLink(FontAwesomeIcon.Language, Loc.Localize("Setup.HelpTranslate", "Help us translate"), KnownPacks.TutorialFor(pack.Code));

                if (PackTracker(pages, i) is { Length: > 0 } tracker)
                {
                    ImGui.SameLine();
                    IconLink(FontAwesomeIcon.Bug, Loc.Localize("Setup.Report", "Report a localization issue"), tracker);
                }
            }

            // Only with something installed: this plugin never offers a language of its own.
            if (this.config.LanguagePackPath.Length > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (this.LanguageRow("english", chosen == English, null, FontAwesomeIcon.Globe, ChoiceLabel(English))
                    && chosen != English)
                {
                    this.config.ServeLanguagePack = false;
                    chosen = English;
                    this.NoteServing();
                    changed = true;
                }
            }

            // Not a language: sets no address, and opens the folder row instead.
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (this.LanguageRow("own", chosen == OwnPack, null, FontAwesomeIcon.FolderOpen, ChoiceLabel(OwnPack)))
            {
                this.chosenPack = OwnPack;
                chosen = OwnPack;
                this.ServeAgain();
                changed = true;
            }
        }

        if (chosen >= 0 && KnownPacks.All[chosen].Published)
        {
            this.DrawPackAction(pages, chosen);
        }

        return chosen;
    }

    /// <summary>An icon that opens a page in the browser. The label and the address go in the tooltip.</summary>
    private static void IconLink(FontAwesomeIcon icon, string label, string url)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Blue))
        using (ImRaii.PushFont(UiBuilder.IconFont, true))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        SetTooltip(label + "\n" + url);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Util.OpenLink(url);
        }
    }

    /// <summary>A radio button, then a flag or a glyph, then the name. True when the radio was pressed.</summary>
    private bool LanguageRow(string id, bool selected, string? flagCode, FontAwesomeIcon? glyph, string label, bool disabled = false)
    {
        var rowY = ImGui.GetCursorPosY();
        bool pressed;
        using (ImRaii.Disabled(disabled))
        {
            pressed = ImGui.RadioButton($"##pick_{id}", selected);
        }

        ImGui.SameLine();

        if (flagCode is not null && this.Flag(flagCode) is { } flag)
        {
            ImGui.SetCursorPosY(rowY + (ImGui.GetFrameHeight() - FlagHeight) / 2f);
            ImGui.Image(flag.Handle, new Vector2(FlagHeight * flag.Width / flag.Height, FlagHeight));
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
        }
        else if (glyph is { } icon)
        {
            ImGui.AlignTextToFramePadding();
            using (ImRaii.PushFont(UiBuilder.IconFont, true))
            {
                ImGui.TextUnformatted(icon.ToIconString());
            }

            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        return pressed;
    }

    /// <summary>The Translated cell: a bar with the figure, "No pack yet", or blank while nothing is known.</summary>
    /// <returns>The manifest the figure came from, for the cells after it.</returns>
    private static void DrawCoverageCell(KnownPack pack, PackManifest? manifest, bool loading)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();

        if (!pack.Published)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.NoPackYet", "No pack yet"));
        }
        else if (loading)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.CoverageLoading", "Loading..."));
        }
        else if (manifest is null)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.Offline", "Offline"));
        }
        else if (manifest.Coverage is { } coverage)
        {
            var percent = coverage.Percent;
            var overlay = percent >= 100 ? "100 %"
                : percent is > 0 and < 1 ? "<1 %"
                : percent.ToString("0.#", CultureInfo.InvariantCulture) + " %";

            ImGui.ProgressBar((float)Math.Clamp(percent / 100.0, 0.0, 1.0), new Vector2(-1, 0), overlay);
        }
    }

   
    // The manifest published at a pack's release, fetched once per session. 
    private (PackManifest? Manifest, bool Loading) PublishedManifest(KnownPack pack)
    {
        lock (this.published)
        {
            if (this.published.TryGetValue(pack.Code, out var known))
            {
                return (known, false);
            }

            if (pack.ManifestUrl is not { } url)
            {
                return (null, false);
            }

            if (this.fetching.Add(pack.Code))
            {
                _ = this.installer.FetchManifestAsync(url).ContinueWith(t =>
                {
                    lock (this.published)
                    {
                        this.published[pack.Code] = t.IsCompletedSuccessfully ? t.Result : null;
                    }
                });
            }

            return (null, true);
        }
    }

    /// <summary>
    ///     The one thing to press about the pack, which is a different thing in three states.
    /// </summary>
    /// <remarks>One button: at any moment there is one useful thing to do to a pack.</remarks>
    private void DrawPackAction(PageStatus pages, int chosen)
    {
        var source = this.config.PackSource.Trim();
        var busy = this.installing || pages.Update.State == UpdateState.Checking;

        // A folder never offers updates, and stays pressable once adopted: pressing it again after a
        // rebuild is the development loop.
        if (chosen == OwnPack)
        {
            var folder = this.config.OwnPackFolder.Trim();

            ActionButton(
                Loc.Localize("Setup.UseFolder", "Use this folder"),
                Loc.Localize("Setup.UseFolderTip", "Serves the folder in place. Press again after a rebuild."),
                // Not `busy`: adopting resets the update state to Checking, and a folder never runs a
                // check that would clear it, so the button would stay disabled for the session.
                this.installing || folder.Length == 0 || this.FolderPack().Manifest is null,
                () =>
                {
                    // The rest of the window works off PackSource, so adopting is what moves the
                    // folder into it. Kept in its own field as well, so a language chosen later does
                    // not take the path with it.
                    this.config.PackSource = folder;
                    this.Install(folder);
                },
                alignRight: false);
            return;
        }

        // Installed FROM THIS SOURCE, not "something is installed": another language chosen leaves a
        // pack loaded that this must offer to replace rather than to check.
        var current = pages.Manifest is not null
                      && source.Length > 0
                      && string.Equals(source, this.config.InstalledFrom, StringComparison.OrdinalIgnoreCase);

        // The pack moved: the chooser resolved it through the manifest's language, and the listed
        // address is not the one this install fetches from. The old address keeps answering only as
        // long as its publisher mirrors there, so the one useful thing to press is the move itself.
        if (current
            && chosen >= 0
            && KnownPacks.All[chosen].Source is { } listed
            && !string.Equals(listed, source, StringComparison.OrdinalIgnoreCase))
        {
            ActionButton(
                Loc.Localize("Update.Move", "Reinstall from the new address"),
                Loc.Localize("Update.MoveTip", "The pack moved. Reinstall from the new address to keep receiving updates."),
                busy,
                () =>
                {
                    this.config.PackSource = listed;
                    this.Install(listed);
                });
            return;
        }

        if (current && pages.Update is { State: UpdateState.Available, Published: { } published })
        {
            // Reinstalls from where this one came from: the manifest carries no download address, so
            // successive versions are expected at a stable one.
            ActionButton(
                string.Format(
                    Loc.Localize("Update.InstallNew", "Install new version {0}"),
                    published.TranslationVersion ?? Loc.Localize("Pack.Unversioned", "unversioned")),
                null,
                busy,
                () => this.Install(this.config.PackSource));
            return;
        }

        if (current)
        {
            var declared = pages.Update.State != UpdateState.NotDeclared;

            ActionButton(
                Loc.Localize("Update.Check", "Check for updates"),
                null,
                busy || !declared,
                this.checkForUpdate);
            return;
        }

        ActionButton(
            Loc.Localize("Setup.Install", "Install"),
            null,
            busy || source.Length == 0,
            () => this.Install(this.config.PackSource));
    }

    /// <summary>One button with one id, so that changing its label does not make it a new widget.</summary>
    private static void ActionButton(string label, string? tooltip, bool disabled, Action press, bool alignRight = true)
    {
        if (alignRight)
        {
            var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - width));
        }
        using (ImRaii.Disabled(disabled))
        {
            if (ImGui.Button($"{label}##packAction"))
            {
                press();
            }
        }

        if (tooltip is { Length: > 0 })
        {
            SetTooltip(tooltip);
        }
    }

    /// <summary>
    ///     The folder a pack was built into, and what this build makes of what is in it.
    /// </summary>
    /// <remarks>
    ///     Drawn only while the chooser is on it. A folder is never copied: the pages are served
    ///     where they lie, so adopting one is reading the manifest and writing the path down.
    /// </remarks>
    private void DrawOwnPack(PageStatus pages, ref bool changed)
    {
        var folder = this.config.OwnPackFolder;

        // Two buttons sit to the right of the box, Browse and the folder's own action, so the box
        // gives back the width of both rather than a fixed strip.
        var browse = Loc.Localize("Setup.Browse", "Browse...");
        var use = Loc.Localize("Setup.UseFolder", "Use this folder");
        var style = ImGui.GetStyle();
        var strip = ImGui.CalcTextSize(browse).X + ImGui.CalcTextSize(use).X
                    + (style.FramePadding.X * 4) + (style.ItemSpacing.X * 2);

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Loc.Localize("Setup.OwnLabel", "Custom Language Pack directory"));
        ImGui.SameLine();

        ImGui.SetNextItemWidth(-strip);
        if (ImGui.InputText("##packSource", ref folder, 2048))
        {
            this.config.OwnPackFolder = folder;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button($"{browse}##packSource"))
        {
            this.BrowseForLanguagePack();
        }

        ImGui.SameLine();
        this.DrawPackAction(pages, OwnPack);

        this.DrawFolderVerdict();
    }

    /// <summary>
    ///     Whether what is in that folder is a pack this client can be given, said before it is.
    /// </summary>
    /// <remarks>
    ///     The patch gate is the one worth seeing early: a pack built against another patch adopts
    ///     cleanly and is refused at the next start.
    /// </remarks>
    private void DrawFolderVerdict()
    {
        if (this.config.OwnPackFolder.Trim().Length == 0)
        {
            ImGui.TextDisabled(Loc.Localize("Setup.OwnEmpty",
                "The folder holding gubal-manifest.json."));
            return;
        }

        var (manifest, error) = this.FolderPack();

        if (manifest is null)
        {
            Icon(FontAwesomeIcon.ExclamationTriangle, Red);
            ImGui.TextWrapped(error ?? string.Empty);
            ImGui.PopStyleColor();
            return;
        }

        var built = manifest.GameVersion;
        var running = this.RunningGame();

        if (built is { Length: > 0 } && running is { Length: > 0 } && !string.Equals(built, running, StringComparison.Ordinal))
        {
            Icon(FontAwesomeIcon.ExclamationTriangle, Amber);
            ImGui.TextWrapped(string.Format(
                Loc.Localize("Setup.OwnWrongPatch",
                    "{0}, built for game {1}. The game is running {2}, so it will be refused at "
                    + "startup rather than served. Rebuild it."),
                manifest.DisplayName,
                built,
                running));
            ImGui.PopStyleColor();
            return;
        }

        Icon(FontAwesomeIcon.Check, Green);
        ImGui.TextWrapped(string.Format(
            Loc.Localize("Setup.OwnReady", "{0} ({1}), built for this patch."),
            manifest.DisplayName,
            manifest.TranslationVersion ?? Loc.Localize("Pack.Unversioned", "unversioned")));
        ImGui.PopStyleColor();
    }

    /// <summary>What is in the folder now, re-read only when the path in the box changes.</summary>
    /// <remarks>
    ///     Memoised: this is a disk read and the caller is a draw loop. It does not notice a rebuild
    ///     under an unchanged path, which is what "Use this folder" is for.
    /// </remarks>
    private (PackManifest? Manifest, string? Error) FolderPack()
    {
        var folder = this.config.OwnPackFolder.Trim();
        if (string.Equals(this.checkedPath, folder, StringComparison.OrdinalIgnoreCase))
        {
            return this.checkedPack;
        }

        this.checkedPath = folder;
        this.checkedPack = Directory.Exists(folder)
            ? PackManifest.Read(folder)
            : (null, string.Format(Loc.Localize("Setup.OwnNoFolder", "No folder at '{0}'."), folder));

        return this.checkedPack;
    }

    /// <summary>The patch the client is running, read once. It cannot change while it is running.</summary>
    private string? RunningGame() => this.runningGame ??= ExdRedirector.RunningGameVersion() ?? string.Empty;

    /// <summary>Said in place of the action button for a language nobody publishes a pack for.</summary>
    private static void DrawUnbuiltLanguage(KnownPack pack)
    {
        ImGui.TextWrapped(Loc.Localize("Setup.Unbuilt",
            "No language pack available. Want to help translate it?"));

        Link(Loc.Localize("Recruit.Ask", "Ask on GitHub Discussions"), KnownPacks.Discussions);
    }

    /// <summary>The chooser entry the stored source resolves to, worked out once and then remembered.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Array.FindIndex{T}(T[], Predicate{T})" /> answers -1 for an address that is
    ///         nobody's published pack, which is <see cref="OwnPack" /> by construction.
    ///     </para>
    ///     <para>
    ///         A link that matches no entry is still a published pack when the installed manifest
    ///         names a listed language: the pack moved repositories after this install, and the
    ///         chooser shows its language rather than "Choose one". <see cref="DrawPackAction" />
    ///         then offers the new address. A folder never resolves this way; it is somebody's own.
    ///     </para>
    /// </remarks>
    private int Chosen(PageStatus pages)
    {
        // Derived every frame, not remembered: /gubal usepack flips it from chat.
        if (this.config.LanguagePackPath.Length > 0 && !this.config.ServeLanguagePack)
        {
            return English;
        }

        if (this.chosenPack is { } already)
        {
            return already;
        }

        var source = this.config.PackSource.Trim();
        if (this.config.LanguagePackPath.Length > 0)
        {
            if (!string.Equals(this.config.LanguagePackPath, this.installer.InstalledPath, StringComparison.OrdinalIgnoreCase))
            {
                source = this.config.LanguagePackPath;
                this.config.OwnPackFolder = source;
            }
            else if (this.config.InstalledFrom.Length > 0)
            {
                source = this.config.InstalledFrom.Trim();
            }

            this.config.PackSource = source;
        }

        var index = source.Length == 0
            ? NoChoice
            : Array.FindIndex(
                KnownPacks.All,
                p => p.Source is { } s && string.Equals(s, source, StringComparison.OrdinalIgnoreCase));

        if (index < 0 && PackInstaller.IsRemote(source) && KnownPacks.ForCode(pages.Manifest?.Language) is { } moved)
        {
            index = Array.IndexOf(KnownPacks.All, moved);
        }

        this.chosenPack = index;
        return index;
    }

    private static string ChoiceLabel(int chosen) => chosen switch
    {
        English => Loc.Localize("Setup.LanguageEnglish", "English (no localization)"),
        OwnPack => Loc.Localize("Setup.LanguageOwn", "A pack of your own"),
        NoChoice => Loc.Localize("Setup.LanguageNone", "Choose one"),
        _ => KnownPacks.All[chosen].Name,
    };

    /// <summary>Serves the installed pack again, when the chooser was showing the game's English.</summary>
    /// <remarks>Only then: re-picking the language already showing must raise no banner.</remarks>
    private void ServeAgain()
    {
        if (this.config.ServeLanguagePack)
        {
            return;
        }

        this.config.ServeLanguagePack = true;
        this.NoteServing();
    }

    /// <summary>Raises the restart banner for switching the translation on or off.</summary>
    private void NoteServing()
    {
        this.restartReason ??=
            Loc.Localize("Restart.Serving",
                "You changed the language. The game reads all of its text once, at startup, so this "
                + "takes effect the next time it starts.");
    }

    /// <summary>Shows automatic updates only for an installed remote pack.</summary>
    private void DrawAutoUpdateRow()
    {
        var available = this.config.LanguagePackPath.Length > 0
                        && PackInstaller.IsRemote(this.config.PackSource)
                        && string.Equals(
                            this.config.LanguagePackPath,
                            this.installer.InstalledPath,
                            StringComparison.OrdinalIgnoreCase);

        if (!available)
        {
            return;
        }

        var auto = this.config.AutoUpdatePack;
        if (ImGui.Checkbox(Loc.Localize("Setup.Auto", "Auto-update at start"), ref auto))
        {
            this.setAutoUpdate(auto);
        }

        if (auto)
        {
            ImGui.Indent();
            this.DrawBootWaitState();
            ImGui.Unindent();
        }
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
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGui.TextWrapped(Loc.Localize("Boot.NotWaiting",
            "Dalamud is not waiting for plugins at startup, so nothing is fetched then. Turn it on in "
            + "Dalamud's settings."));
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
                this.config.InstalledFrom = source;
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

    /// <summary>What the loaded pack is, who made it, and whether it is reaching the game.</summary>
    private static void DrawPackDetail(PackManifest pack, PageStatus pages)
    {
        var language = pack.LanguageName ?? pack.Language ?? Loc.Localize("Pack.UnknownLanguage", "unknown language");
        var identity = pack.Author is { Length: > 0 } author
            ? string.Format(Loc.Localize("Pack.LanguageByAuthor", "{0} by {1}"), language, author)
            : language;

        var detail = identity + " | " + string.Format(
            Loc.Localize("Pack.BuiltFor", "Built for game {0}"),
            pack.GameVersion ?? Loc.Localize("Pack.UnknownVersion", "unknown"));

        if (pages.Active)
        {
            detail += " | " + string.Format(
                Loc.Localize("Pack.Served", "{0} read(s) answered from disk this session"),
                pages.ServedCount.ToString("N0"));
        }

        ImGui.TextDisabled(detail);

        if (pages.Active)
        {
            // Show the fonts only when the pack has some. Show served against registered: the
            // client reads the fonts once at boot, so "registered, not served" is the fault to find.
            if (pages.FontCount > 0)
            {
                ImGui.TextDisabled(string.Format(
                    Loc.Localize("Pack.Fonts", "{0} of {1} font file(s) served from the pack"),
                    pages.FontsServedCount.ToString("N0"),
                    pages.FontCount.ToString("N0")));
            }
        }

    }

    /// <summary>Bug reports and contact details.</summary>
    private static void DrawHelpTab()
    {
        Icon(FontAwesomeIcon.Bug, Blue);
        Link(Loc.Localize("Help.ReportBug", "Report a bug"), KnownPacks.PluginIssues);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Icon(FontAwesomeIcon.Comments, Blue);
        ImGui.TextDisabled(Loc.Localize("Contact.Discord", "Discord"));
        ImGui.PopStyleColor();
        ImGui.SameLine();

        // Selectable rather than text: a handle has to be copyable to be worth anything.
        if (ImGui.Selectable(KnownPacks.Discord, false, ImGuiSelectableFlags.None,
                ImGui.CalcTextSize(KnownPacks.Discord)))
        {
            ImGui.SetClipboardText(KnownPacks.Discord);
        }

        SetTooltip(Loc.Localize("Contact.Copy", "Click to copy."));
    }

    /// <summary>Where a wrong line in the chosen pack is reported, or null when nowhere is known.</summary>
    /// <remarks>
    ///     The installed manifest wins, but only when it is the chosen language: otherwise a report
    ///     about Italian would go to whoever maintains Spanish.
    /// </remarks>
    private static string? PackTracker(PageStatus pages, int chosen)
    {
        var manifest = pages.Manifest;
        var known = chosen >= 0 ? KnownPacks.All[chosen] : KnownPacks.ForCode(manifest?.Language);

        if (known is not { } pack)
        {
            return manifest?.IssuesUrl;
        }

        return string.Equals(pack.Code, manifest?.Language, StringComparison.OrdinalIgnoreCase)
            ? manifest?.IssuesUrl ?? pack.Issues
            : pack.Issues;
    }

    /// <summary>Text that opens a page in the browser. Never drawn for an address we lack.</summary>
    /// <remarks>
    ///     Underlined on hover and never otherwise: this window is full of coloured text that does
    ///     nothing, so colour alone does not mark a link. The address goes in the tooltip.
    /// </remarks>
    private static void Link(string label, string url)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Blue))
        {
            ImGui.TextUnformatted(label);
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList()
            .AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), ImGui.GetColorU32(Blue));

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        SetTooltip(url);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Util.OpenLink(url);
        }
    }

    /// <summary>
    ///     Picks the folder a pack was built into, and fills the box with it.
    /// </summary>
    /// <remarks>Fills the box and stops there, so the verdict under it can be read first.</remarks>
    private void BrowseForLanguagePack()
    {
        var startPath = this.config.LanguagePackPath;
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            startPath = string.Empty;
        }

        this.fileDialogs.OpenFolderDialog(
            Loc.Localize("Setup.PickerTitle", "Select the folder your language pack was built into"),
            (confirmed, selected) =>
            {
                if (!confirmed || string.IsNullOrWhiteSpace(selected))
                {
                    return;
                }

                this.config.OwnPackFolder = selected;
                this.save(this.config);
            },
            startPath,
            isModal: false);
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
/// <param name="FontCount">Font files the pack registered. Zero for most packs.</param>
/// <param name="FontsServedCount">Font reads answered from disk. Zero with fonts registered means the client read them before the hook.</param>
/// <param name="Error">Why it is not installed, when it is not. Null when it is, or when nobody asked.</param>
/// <param name="Manifest">What the loaded pack says about itself. Null when none loaded.</param>
/// <param name="Update">What the background check made of the pack's declared update address.</param>
internal readonly record struct PageStatus(
    bool Active,
    int PageCount,
    int ServedCount,
    int FontCount,
    int FontsServedCount,
    string? Error,
    PackManifest? Manifest,
    UpdateStatus Update);
