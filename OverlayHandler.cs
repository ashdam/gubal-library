using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Translates a simple text overlay: one string in the value array, one text node on screen.
/// </summary>
/// <remarks>
///     <para>
///         Covers <c>TalkSubtitle</c> (cutscene narration), <c>_BattleTalk</c> (combat callouts) and
///         <c>_MiniTalk</c> (speech balloons). All three are the same shape and differ only in name,
///         so they share an implementation rather than three near-copies that drift apart. The
///         <c>Talk</c> window keeps its own handler because it has a second string for the speaker and
///         a wrap width that has to be captured and restored.
///     </para>
///     <para>
///         <b>Both delivery routes are hooked, deliberately.</b> <c>PreRefresh</c> catches text the
///         game hands over as <c>AtkValues</c>; <c>PreDraw</c> catches text it writes straight into the
///         node. Both happen on the same addon in the same scene — narration proved it, where one
///         sentence arrived as a value and the next never produced a refresh at all. Hooking one and
///         reasoning the other away has cost a debugging round twice now.
///     </para>
///     <para>
///         <c>JournalDetail</c> — the journal's quest page, and the duty description behind the Duty
///         Finder — is the one addon that takes the values route alone. That is a measured exception
///         rather than a relaxation of the rule above; see <see cref="ValueOnly" />. It is also the
///         one addon that is not a single line at all, which <see cref="BodyValues" /> covers.
///     </para>
///     <para>
///         Registered against several candidate names at once because Dalamud matches the game's own
///         addon names and this project has no authoritative list of them: <c>AddonBattleTalk</c> does
///         not exist in FFXIVClientStructs at all. A name that never fires costs nothing, and the log
///         reports which one did, so a wrong guess is visible instead of silent.
///     </para>
/// </remarks>
internal sealed unsafe class OverlayHandler : IDisposable
{
    /// <summary>How many refreshes to describe per addon before going quiet.</summary>
    private const int MaxInspections = 8;

    /// <summary>
    ///     Which node<em>s</em> carry text the player reads, for addons whose layout has been observed.
    /// </summary>
    /// <remarks>
    ///     Filled from the miss log rather than guessed. A pass through the Sil'dihn variant dungeon
    ///     produced 44 records with the node id attached, and the split was total: every id 4 was a
    ///     speaker name — Enuo, Darya the Sea-maid, Pari of Plenty, Y'nazqha — and every id 6 was a
    ///     line of dialogue.
    ///     <para>
    ///         Narrowing matters because the fallback scan translates any node holding text, which
    ///         includes the name. With <c>TranslateNpcNames</c> off that is simply wrong, and it also
    ///         fills the miss log with names that are not missing translations at all.
    ///     </para>
    ///     <para>
    ///         It matters for cost too, and for one addon that is the whole reason it is narrowed.
    ///         The id test in <see cref="OnPreDraw" /> runs <em>before</em> the node's text is read,
    ///         and reading a node's text allocates a string. An overlay is on screen for seconds;
    ///         <c>_ToDoList</c> is on screen always, so an unnarrowed sweep of it would allocate a
    ///         string per node per frame for the whole session.
    ///     </para>
    ///     <para>
    ///         A set per addon, because a tracker is not an overlay: it draws the quest name and the
    ///         objective under it, from two different nodes, and one id can only name one of them.
    ///     </para>
    ///     <para>
    ///         An addon not listed here keeps the scan, which is what discovers its layout in the
    ///         first place. That is the intended progression: scan, read the log, add an entry.
    ///     </para>
    /// </remarks>
    private static readonly Dictionary<string, uint[]> BodyNodes = new(StringComparer.Ordinal)
    {
        ["_BattleTalk"] = [6],
        ["_MiniTalk"] = [3],

        // The dialogue choice list — "What will you ask?" over "How fares the realm?", "What of the
        // primals?". Measured with /gubal find on Urianger at the Waking Sands, 8 August 2026:
        //
        //   [find] *** 'SelectString' NODE 2 (written direct, no value): What of the primals?
        //   [find] *** 'SelectString' NODE 2 (written direct, no value): What will you ask?
        //
        // THE PROMPT AND EVERY OPTION SHARE NODE ID 2, which is the whole reason this entry is safe.
        // Each row is a separate component instance of one layout, exactly as a speech balloon is, so
        // the id identifies "a line of this list" rather than one particular line. Narrowing to it
        // skips the window's chrome and keeps every row.
        //
        // It also means the id CANNOT tell the prompt from an option, and nothing here needs it to:
        // both are text the player reads and both are in the corpus. What does matter is that the
        // per-node bookkeeping is keyed by POINTER — see nodeInjected and attempted, which already are,
        // and which is the only reason five rows sharing one id do not overwrite each other's state.
        ["SelectString"] = [2],

        // The quest list down the left of the journal. Measured with /gubal find on "The Price of
        // Principles", 8 August 2026:
        //
        //   [find] *** 'Journal' NODE 3 (written direct, no value): The Price of Principles
        //
        // Narrowed rather than swept, though this window is only open when the player opens it. The
        // list interleaves quest rows with place headers — Limsa Lominsa, Ul'dah, The Waking Sands —
        // and those are names this project leaves in English. None of them is in the corpus today, so
        // a sweep would not mistranslate them; it would only record every zone the player has a quest
        // in as a missing translation, which is a miss log that lies about what is missing.
        ["Journal"] = [3],
    };

    /// <summary>Where an addon with no entry in <see cref="BodyValues" /> keeps its text.</summary>
    private static readonly int[] FirstValue = [0];

    /// <summary>First value of <c>JournalDetail</c>'s objective array.</summary>
    private const int FirstObjectiveValue = 188;

    /// <summary>
    ///     How many objective slots that array has.
    /// </summary>
    /// <remarks>
    ///     Read off the layout rather than chosen: a second array of the same length starts at value
    ///     236, one entry per objective, which puts the end of the first at 235. Unused slots are
    ///     <c>Null</c>-typed, never empty strings, so declaring the whole array translates exactly the
    ///     objectives a quest actually has — see <see cref="TranslateValue" />, where a value holding
    ///     no string is refused before anything is looked up.
    /// </remarks>
    private const int ObjectiveValueCount = 236 - FirstObjectiveValue;

    /// <summary>
    ///     Which value<em>s</em> carry player-facing text, for addons where they are not just index 0.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Index 0 held for <c>Talk</c> and <c>TalkSubtitle</c>, which made it look like a
    ///         convention. It is not: <c>_ScreenInfoFront</c>, the banner that narrates variant
    ///         dungeons, puts its line at index 3 of ten. Found by <c>/gubal find</c> rather than by
    ///         reading, because nothing in the struct definitions says so — the addon is not in
    ///         FFXIVClientStructs at all.
    ///     </para>
    ///     <para>
    ///         <b>A set per addon rather than one index, because a panel is not an overlay.</b> An
    ///         overlay draws one line; <c>JournalDetail</c> draws a quest's title, its description and
    ///         its current objective at once, from three separate values. Declaring only the
    ///         description translated the description and left the other two in English — with nothing
    ///         in <c>misses.jsonl</c> to say so, because a value this map does not name is never looked
    ///         up at all.
    ///     </para>
    ///     <para>
    ///         Named indices, not "translate every string value" as <see cref="ListValues" /> does.
    ///         <c>JournalDetail</c> carries 330 values, and among them are the instance names this
    ///         project pins to English — see <see cref="ValueOnly" />, where translating them through
    ///         the node sweep is recorded as a defect. A list's options are all of one kind and a
    ///         panel's values are not.
    ///     </para>
    /// </remarks>
    private static readonly Dictionary<string, int[]> BodyValues = new(StringComparer.Ordinal)
    {
        ["_ScreenInfoFront"] = [3],

        // The duty description in the Duty Finder. Found with /gubal find on a guildhest: selecting
        // one logs the same line arriving at 'ContentsFinder' value 1475 of 1830, at 'JournalDetail'
        // value 12 of 330, and at 'JournalDetail' node 8 — three sightings, one of which is the one
        // to use.
        //
        // JournalDetail, not ContentsFinder: the finder carries the string but draws it nowhere, and
        // its .uld agrees — its largest text node is 414x21, a single line.
        //
        // The same panel is the quest page of the journal, and there value 12 is only the middle of
        // three things the player reads. Measured from two /gubal probe dumps of the panel, 8 August
        // 2026 — "Forging Northwards" with one objective and "The Price of Principles" with five:
        //
        //   [2]   Lv. 50                                    the level chip
        //   [5]   The Price of Principles                   THE TITLE
        //   [12]  Ever since your famous victory over…      the description
        //   [13]  Minfilia                                  the quest giver's name
        //   [136] Completion Bonus                          a UI label
        //   [187] UInt 5                                    how many objectives follow
        //   [188] Speak with Y'shtola.                      THE OBJECTIVES, one per value
        //   …
        //   [192] Speak with Urianger.
        //   [263] Map  [264] Abandon  [265] Retry           the buttons
        //   [270] Ever since your famous victory…           THE SUMMARY
        //
        // Value 12 is the description of the CURRENT STAGE and 270 is the quest's opening text, which
        // the panel prints under "Summary". They look interchangeable at a quest's first stage and are
        // not: there the two hold the same sentence and 270 is Null, so a dump taken then says nothing
        // about it. Only once "The Price of Principles" had advanced did they separate — 12 became "As
        // expected, the other Scions are deeply concerned…" and 270 kept the opening — and a build
        // carrying 12 alone drew a Spanish description above an English summary.
        //
        // Only the three the corpus is written for. Everything else on that list is either the game's
        // own chrome, which this project pins to English, or an NPC name, which is TranslateNpcNames'
        // decision and not this map's — and declaring one would put every quest giver in the miss log.
        //
        // The array is declared whole rather than trimmed to value 187's count. The count corroborates
        // the layout (it read 1 for the one-objective quest and 5 for the five-objective one) but
        // nothing has to trust it: an unused slot is Null-typed and refused before it is looked up.
        ["JournalDetail"] = [5, 12, 270, .. Enumerable.Range(FirstObjectiveValue, ObjectiveValueCount)],
    };

    /// <summary>
    ///     Addons handled through their values alone, with the node sweep skipped entirely.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The exception to "hook both routes, always". Everywhere else the two routes carry
    ///         different lines and hooking one silently half-works. Here they carry the <em>same</em>
    ///         line in two forms, and only one of the two can ever match.
    ///     </para>
    ///     <para>
    ///         <b>The node has the game's line wrapping baked into it, and loses the paragraph
    ///         break doing so.</b> Measured on <c>JournalDetail</c> with a guildhest: value 12 holds
    ///         the description intact, blank line and all, while node 8 holds it broken into
    ///         52-character lines and reading <c>…barricades.Slay the goblins…</c> — the two
    ///         paragraphs run together with no separator at all. <see cref="TextKey.Normalize" />
    ///         collapses the corpus source to <c>…barricades. Slay the goblins…</c>, with the space,
    ///         so the node text is a key that cannot exist. Every frame of it would be a lookup that
    ///         is guaranteed to fail.
    ///     </para>
    ///     <para>
    ///         Skipping it is not merely an optimisation. The first run with the sweep left on
    ///         recorded our own Spanish coming back through node 8 as a missing translation, and put
    ///         the instance names from node 38 — Flicking Sticks and Taking Names, Solemn Trinity —
    ///         through the corpus, which is exactly the text this project pins to English.
    ///     </para>
    ///     <para>
    ///         Skipping the sweep costs this panel nothing, and that is worth stating rather than
    ///         assuming: the title and the objectives are drawn from values too, so the whole page is
    ///         reachable through the one route. See <see cref="BodyValues" /> for which values.
    ///     </para>
    /// </remarks>
    private static readonly HashSet<string> ValueOnly = new(StringComparer.Ordinal)
    {
        "JournalDetail",
    };

    /// <summary>
    ///     Addons carrying MANY translatable strings in one value array, rather than one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A choice list is not a bigger overlay, it is a different shape: five options and a
    ///         prompt arrive together, and <see cref="BodyValue" /> can only name one index. Every
    ///         string-typed value is translated instead, which needs no index map at all — a value that
    ///         is a number, a flag or a string with no translation is left exactly as it was.
    ///     </para>
    ///     <para>
    ///         <b>Added because the node route alone flickers, visibly.</b> With only
    ///         <c>BodyNode["SelectString"]</c> in place the list drew correct Spanish, and then
    ///         clicking an option showed English for an instant before it corrected itself. That is the
    ///         two routes racing: the click refreshes the addon, the game rebuilds each node from the
    ///         English in its <c>AtkValues</c>, and <c>PreDraw</c> only translates it back on the
    ///         following frame. Writing the values means the game draws Spanish the first time and
    ///         there is no frame to catch.
    ///     </para>
    ///     <para>
    ///         The node route stays on for these addons, deliberately. It is what covers a list the
    ///         game populates without a refresh, and it is now cheap: the values it would otherwise
    ///         re-translate are recognised as ours through <see cref="valueInjectedKey" />.
    ///     </para>
    /// </remarks>
    private static readonly HashSet<string> ListValues = new(StringComparer.Ordinal)
    {
        "SelectString",
        "SelectIconString",
    };

    /// <summary>
    ///     Addons that still get translated, but whose misses are only recorded while the event probe
    ///     is on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>_ScreenInfoFront</c> is the screen-centre overlay, and its value 3 carries two
    ///         completely different kinds of text down one channel: the narration banner in variant
    ///         dungeons, which this plugin exists to translate, and the game's own UI toasts, which it
    ///         must never touch. A measured session recorded ten distinct misses through this addon and
    ///         <em>all ten</em> were toasts — "Target out of range.", "Invalid target.", an item
    ///         obtained, a quest objective tally, a market board sale. None of them can ever be in the
    ///         corpus, and together they were the entire miss log for that session.
    ///     </para>
    ///     <para>
    ///         <b>Suppressed rather than filtered, because nothing observed separates the two.</b> The
    ///         value index does not: both arrive at 3. The text does not: a toast and a banner line are
    ///         both ordinary sentences. Inventing a discriminator here — a length threshold, a keyword
    ///         list — would be a guess that fails silently on the day it is wrong, which is the failure
    ///         mode every other note in this file is about.
    ///     </para>
    ///     <para>
    ///         The diagnostic is not thrown away, only moved behind <c>/gubal probe</c> — which is
    ///         already what you turn on when you are investigating one addon, and which is on when
    ///         anyone is standing in a variant dungeon asking why the banner is still English. What
    ///         would retire this entry is a dump of the addon in a variant dungeon showing a node or
    ///         value that carries the narration alone; until someone has one, there is nothing to
    ///         narrow to.
    ///     </para>
    ///     <para>
    ///         Injection is deliberately untouched. Translating the banner is a shipped feature, and
    ///         a toast only reaches the corpus lookup, misses, and leaves the game's own text alone.
    ///     </para>
    /// </remarks>
    private static readonly HashSet<string> MissesOnlyWhileProbing = new(StringComparer.Ordinal)
    {
        "_ScreenInfoFront",
    };

    private readonly string[] addonNames;
    private readonly Configuration config;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly MissLog misses;
    private readonly MacroResolver resolver;

    /// <summary>Source of the territory scope; see <see cref="EventContext.ActiveScope" />.</summary>
    private readonly IClientState clientState;
    private readonly TranslationStore store;

    private readonly IAddonLifecycle.AddonEventDelegate onPreDraw;
    private readonly IAddonLifecycle.AddonEventDelegate onPreRefresh;

    private readonly Dictionary<string, int> inspections = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenAddons = new(StringComparer.Ordinal);

    /// <summary>
    ///     What our own output reads back as on the value route, keyed by addon AND value index.
    /// </summary>
    /// <remarks>
    ///     By index and not by addon alone, because a list writes several values in one refresh — one
    ///     shared entry would let each option overwrite the previous one's guard, so every option after
    ///     the first would be rewritten on every refresh forever. Single-value addons simply have one
    ///     entry, at their own <see cref="BodyValue" /> index.
    /// </remarks>
    private readonly Dictionary<string, string> valueInjected = new(StringComparer.Ordinal);

    /// <summary>
    ///     The same again as lookup keys, so the <em>node</em> route can recognise them too.
    /// </summary>
    /// <remarks>
    ///     Normalized rather than kept verbatim because the two routes see different renderings of one
    ///     line: the value is what we wrote, the node is what the game drew from it, with its own line
    ///     breaks baked in. <see cref="TextKey.Normalize" /> collapses exactly that difference, and it
    ///     is what the lookup would use a moment later anyway.
    ///     <para>
    ///         A SET per addon rather than one string, for the list addons: any of the six strings we
    ///         wrote into a choice list can come back round through any of its nodes, and the node
    ///         route has no way to know which value produced which row.
    ///     </para>
    /// </remarks>
    private readonly Dictionary<string, HashSet<string>> valueInjectedKey = new(StringComparer.Ordinal);

    /// <summary>
    ///     What our own output reads back as on the node route, per <em>node</em>.
    /// </summary>
    /// <remarks>
    ///     <b>Keyed by node pointer, and it has to be.</b> One entry per addon was wrong and the miss
    ///     log proved it: several balloons on screen at once are each a separate component instance of
    ///     the same layout, so every one of their text nodes carries node id 3. One shared entry meant
    ///     the guard only ever recognised the most recent balloon's text, so the others' Spanish came
    ///     back round as though it were fresh English — and got recorded as missing translations for
    ///     lines that had just been translated.
    ///     <para>
    ///         The key is never dereferenced, only compared, so a pointer the game later reuses for a
    ///         different node costs one comparison that fails and re-translates. That is why this can
    ///         be a raw pointer without a lifetime story.
    ///     </para>
    /// </remarks>
    private readonly Dictionary<nint, string> nodeInjected = [];

    /// <summary>Reused across frames so the per-frame node sweep does not allocate.</summary>
    private readonly List<nint> nodeBuffer = [];

    /// <summary>
    ///     Last on-screen text looked up per <em>node</em>, hit or miss, so a miss is not retried.
    /// </summary>
    /// <remarks>
    ///     Keyed by node and not merely by addon, which is how it started and which produced a
    ///     visible defect: <c>TalkSubtitle</c> draws its line through two overlapping text nodes, and
    ///     the second one carries the same string as the first. Sharing one entry per addon meant the
    ///     first node translated, wrote the English into this map, and the second node then matched it
    ///     and was skipped — every frame, permanently, because the state that produced the skip was
    ///     the state the skip preserved. On screen: Spanish over English, both legible.
    ///     <para>
    ///         By pointer rather than by <c>addon#nodeId</c>, which was the same near-miss as
    ///         <see cref="nodeInjected" />: the id is unique within one component, not across the
    ///         several a balloon addon instantiates, so two balloons shared an entry and each blocked
    ///         the other's lookup.
    ///     </para>
    /// </remarks>
    private readonly Dictionary<nint, string> attempted = [];

    /// <summary>Holds the panel padding it measures, so it must outlive a single line.</summary>
    private readonly OverlayLayout layout = new();

    public OverlayHandler(
        IAddonLifecycle lifecycle,
        IPluginLog log,
        Configuration config,
        TranslationStore store,
        MissLog misses,
        MacroResolver resolver,
        IClientState clientState,
        params string[] addonNames)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        this.config = config;
        this.store = store;
        this.misses = misses;
        this.resolver = resolver;
        this.clientState = clientState;
        this.addonNames = addonNames;

        this.onPreRefresh = this.OnPreRefresh;
        this.onPreDraw = this.OnPreDraw;

        this.lifecycle.RegisterListener(AddonEvent.PreRefresh, addonNames, this.onPreRefresh);
        this.lifecycle.RegisterListener(AddonEvent.PreDraw, addonNames, this.onPreDraw);

        this.log.Information("OverlayHandler registered on: {Addons}", string.Join(", ", addonNames));
    }

    public int InjectedCount { get; private set; }

    public void Dispose()
    {
        this.lifecycle.UnregisterListener(AddonEvent.PreRefresh, this.addonNames, this.onPreRefresh);
        this.lifecycle.UnregisterListener(AddonEvent.PreDraw, this.addonNames, this.onPreDraw);
    }

    private void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.config.Enabled || args is not AddonRefreshArgs refresh)
        {
            return;
        }

        var name = args.AddonName;
        this.NoteLive(name, "values");

        var values = (AtkValue*)refresh.AtkValues;
        var count = (int)refresh.AtkValueCount;
        if (values is null || count < 1)
        {
            return;
        }

        if (this.config.ProbeEvents && this.Inspect(name))
        {
            AddonInspector.Dump(this.log, name, (AtkUnitBase*)args.Addon.Address, values, count);
        }

        var scope = EventContext.ActiveScope(this.clientState.TerritoryType, this.log);

        // A list carries every option in one array and no index map can name them; everything else
        // carries its text at known indices. See ListValues.
        if (ListValues.Contains(name))
        {
            for (var i = 0; i < count; i++)
            {
                this.TranslateValue(values, i, name, scope);
            }

            return;
        }

        foreach (var index in BodyValues.GetValueOrDefault(name, FirstValue))
        {
            // Per index, not once for the array: the value count varies with what the addon is
            // showing, so a panel drawing fewer values than usual must still translate the ones it
            // does draw rather than skipping the lot.
            if (index < count)
            {
                this.TranslateValue(values, index, name, scope);
            }
        }
    }

    /// <summary>
    ///     Translates one <c>AtkValue</c> in place, if it holds text this corpus knows.
    /// </summary>
    /// <remarks>
    ///     Every refusal path leaves the value untouched and records nothing, which is what makes it
    ///     safe to call on all of them: a value holding a number, a flag or an untranslated string is
    ///     indistinguishable here from one that simply missed, and both must be left alone.
    /// </remarks>
    private void TranslateValue(AtkValue* values, int index, string name, string? scope)
    {
        var slot = $"{name}#{index}";

        var text = AtkText.ReadString(values[index]);
        if (string.IsNullOrWhiteSpace(text) || text == this.valueInjected.GetValueOrDefault(slot, string.Empty))
        {
            return;
        }

        if (!this.TryTranslate(text, name, slot, scope, out var translated))
        {
            return;
        }

        // This handler never resolved macros at all, which was worse than the lost formatting it is
        // being fixed alongside: it wrote the corpus target verbatim, so a line whose Spanish carries
        // <if(gnum4,cansada,cansado)> put that on screen, angle brackets and all.
        if (!this.resolver.TryResolve(translated, out var resolved, out _))
        {
            return;
        }

        SeStringWriter.Write(&values[index], resolved);

        // Recorded only after a successful write, and holding the read-back rather than the target.
        // An entry here means "our line is already on screen", so writing one on the refusal path
        // above would convince the guard a line it never wrote was in place.
        var readBack = AtkText.ReadString(values[index]);
        this.valueInjected[slot] = readBack;

        if (!this.valueInjectedKey.TryGetValue(name, out var ours))
        {
            ours = new HashSet<string>(StringComparer.Ordinal);
            this.valueInjectedKey[name] = ours;
        }

        ours.Add(TextKey.Normalize(readBack));
        this.InjectedCount++;
    }

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.config.Enabled || ValueOnly.Contains(args.AddonName))
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon is null || !addon->IsVisible)
        {
            return;
        }

        var name = args.AddonName;
        this.NoteLive(name, "nodes");

        // The whole tree, not GetTextNodeById: _MiniTalk keeps its line in a component one level
        // down, where an id lookup on the addon's own list finds nothing at all.
        AddonNodes.CollectTextNodes(addon, this.nodeBuffer);

        // Only for the balloon addons. Both the refit and the dump reinterpret the addon as
        // AddonMiniTalk, and doing that to _BattleTalk would read a different struct's memory as if it
        // were bubble pointers.
        var isMiniTalk = name is "_MiniTalk" or "MiniTalk";

        // Characterising an unknown addon: what nodes it has and how they are set up. Budgeted, unlike
        // the balloon dumps below, because it answers a question about the addon rather than about a
        // line — a handful of frames shows the layout and repeating it forever shows nothing new.
        if (this.config.ProbeEvents && this.Inspect(name))
        {
            AddonInspector.DumpTextNodes(this.log, name, "layout", this.nodeBuffer);

            // The whole tree, not just the text. Whether a longer translation fits depends on what is
            // drawn behind it, and the text nodes say nothing about that.
            AddonInspector.DumpAllNodes(this.log, name, addon);
        }

        var known = BodyNodes.TryGetValue(name, out var bodyIds);

        // Resolved once for the whole sweep rather than once per node. It walks every event handler
        // the client has loaded — 629 in a typical session — and the answer cannot change between
        // two nodes of the same addon in the same frame, so asking twice was only ever waste. This is
        // most of what the per-node guard below was protecting against.
        var conversation = EventContext.ActiveScope(this.clientState.TerritoryType, this.log);

        foreach (var pointer in this.nodeBuffer)
        {
            var node = (AtkTextNode*)pointer;
            var id = node->NodeId;

            // A characterised addon gets exactly its body nodes; an unknown one gets every node, so
            // the miss log reveals its layout. That is how every known id here was established.
            //
            // Before the text is read, deliberately. Array.IndexOf over two or three ids is cheaper
            // than the string allocation ReadNodeText would do, and for an addon that is on screen
            // every frame that difference is the entire cost of listing it.
            if (known && Array.IndexOf(bodyIds!, id) < 0)
            {
                continue;
            }

            var onScreen = AtkText.ReadNodeText(node);
            if (onScreen.Length == 0 || onScreen == this.nodeInjected.GetValueOrDefault(pointer, string.Empty))
            {
                continue;
            }

            // Two guards, not one. The first skips our own output; this one skips a line we already
            // looked up and did not find. Without it a miss repeats every frame for as long as the
            // line is on screen — and a lookup is not free: it normalises the key and walks all 629
            // loaded event handlers to resolve the conversation. In a duty where nothing is
            // translated yet, that is the whole run.
            //
            // Per node. Sibling nodes showing the same string are not a repeat of one lookup, they
            // are two nodes that both need writing.
            if (onScreen == this.attempted.GetValueOrDefault(pointer, string.Empty))
            {
                continue;
            }

            this.attempted[pointer] = onScreen;

            // Our own line arriving back through the other route. TalkSubtitle takes both: the value
            // route writes the Spanish, the game draws it into this node, and the node route reads it
            // as if it were fresh source text. nodeInjected cannot catch that, because this node was
            // never written *here* — so the lookup missed and misses.jsonl gained
            // {"Key":"Y así fue como nuestro barco se separó del muelle.","Speaker":"TalkSubtitle#4"},
            // which is a line out of our own translations/VoiceMan_07000.json.
            //
            // Nothing was wrong on screen; the cost was a miss log claiming translated lines need
            // translating, which is the one thing that file exists to tell the truth about.
            //
            // Placed after the attempted guard rather than before it, so the normalization runs once
            // per new line per node instead of once per frame — the same reason that guard exists.
            if (this.valueInjectedKey.TryGetValue(name, out var ours)
                && ours.Contains(TextKey.Normalize(onScreen)))
            {
                // Recorded as ours, not merely skipped, so every later frame settles it on the cheap
                // string compare at the top of the loop.
                this.nodeInjected[pointer] = onScreen;
                continue;
            }

            // The node id goes into the miss record, not just the addon name. Without it the log
            // cannot distinguish the body from the speaker: a _BattleTalk pass recorded the NPC name
            // "Y'nazqha" as a missed line alongside two real ones, and there was no way to tell which
            // node each came from. Learn the layout from the log, then narrow this scan to the body.
            if (!this.TryTranslate(onScreen, name, $"{name}#{id}", conversation, out var translated))
            {
                continue;
            }

            if (!this.resolver.TryResolve(translated, out var resolved, out _))
            {
                // attempted[pointer] is already set above, so a refusal is not retried next frame.
                continue;
            }

            // Gated on the write, not on a frame budget. PreDraw runs every frame, so an
            // inspection counter is spent within a few frames of the first line and every dump after
            // that is lost — which is how eight identical dumps of one untranslated balloon were
            // collected while the injected ones went unrecorded. One line injected, one pair of dumps.
            // Every overlay, not just balloons. Restricting this to _MiniTalk left the banners with only
            // the budgeted layout dump, which is spent within a few frames of the first line — so a
            // guildhest reporting several overflowing lines produced evidence for none of them.
            var probing = this.config.ProbeEvents;
            if (probing)
            {
                if (isMiniTalk)
                {
                    AddonInspector.DumpMiniTalk(this.log, addon, "before inject");
                }
                else
                {
                    AddonInspector.DumpAllNodes(this.log, name, addon, "before inject");
                }
            }

            // Before the write, because it can only learn the panel's chrome while the node still holds
            // the game's own line at the height the game gave it.
            if (!isMiniTalk)
            {
                this.layout.Learn(addon, node);
            }

            SeStringWriter.Write(node, resolved);

            // Two layout models, and which one applies is written on the node. A balloon has no
            // WordWrap: the game breaks its lines only where the text says so and widens the balloon,
            // so the translation needs more WIDTH. A battle-talk banner has WordWrap and a fixed
            // column: the game reflows the line, so a longer translation needs more HEIGHT.
            //
            // Getting this backwards is not a near miss. Widening a wrapping node does nothing, and
            // wrapping a widening one collapses it into a column — both were tried on balloons before
            // the flags were read.
            if (isMiniTalk)
            {
                BalloonLayout.Fit(addon, node);
            }
            else
            {
                this.layout.Fit(addon, node);
            }

            // After the refit, so it is the geometry actually on screen.
            if (probing)
            {
                if (isMiniTalk)
                {
                    AddonInspector.DumpMiniTalk(this.log, addon, "after inject");
                }
                else
                {
                    AddonInspector.DumpAllNodes(this.log, name, addon, "after inject");
                }
            }

            this.nodeInjected[pointer] = AtkText.ReadNodeText(node);
            this.InjectedCount++;
        }
    }

    /// <param name="addon">
    ///     The bare addon name. Separate from <paramref name="label" /> because the recording rules key
    ///     on the addon, and the node route's label carries a node id glued to it.
    /// </param>
    /// <param name="label">What identifies the line in the miss record: the addon, plus the node id
    ///     where the text came off a node.</param>
    private bool TryTranslate(string text, string addon, string label, string? conversation, out string translated)
    {
        var key = TextKey.Normalize(text);

        if (this.store.TryGetTranslation(conversation, key, out translated))
        {
            return true;
        }

        // Two conditions, and they answer different questions. LogMisses is the user asking for a miss
        // log at all; the second is whether this particular addon's misses are worth anything right
        // now. See MissesOnlyWhileProbing.
        if (this.config.LogMisses
            && (this.config.ProbeEvents || !MissesOnlyWhileProbing.Contains(addon)))
        {
            this.misses.Record(key, text, label);
        }

        return false;
    }

    /// <summary>
    ///     Records, once per addon and route, that this handler has actually seen it.
    /// </summary>
    /// <remarks>
    ///     <b>Both routes, and the route is named.</b> This used to log only from the value path, which
    ///     made it a misleading signal rather than a useful one: <c>_MiniTalk</c> carries no
    ///     <c>AtkValues</c> at all, so it can be handled every frame through the node path and still
    ///     never appear in the log. Reading its absence as "that addon never fired" is exactly the
    ///     wrong conclusion, and it was drawn.
    /// </remarks>
    private void NoteLive(string addonName, string route)
    {
        if (this.seenAddons.Add($"{addonName}#{route}"))
        {
            this.log.Information("Overlay addon '{Addon}' is live (via {Route}).", addonName, route);
        }
    }

    private bool Inspect(string addonName)
    {
        var seen = this.inspections.GetValueOrDefault(addonName);
        if (seen >= MaxInspections)
        {
            return false;
        }

        this.inspections[addonName] = seen + 1;
        return true;
    }
}
