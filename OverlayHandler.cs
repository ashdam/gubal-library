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
    ///     Which node carries the dialogue body, for addons whose layout has been observed.
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
    ///         An addon not listed here keeps the scan, which is what discovers its layout in the
    ///         first place. That is the intended progression: scan, read the log, add an entry.
    ///     </para>
    /// </remarks>
    private static readonly Dictionary<string, uint> BodyNode = new(StringComparer.Ordinal)
    {
        ["_BattleTalk"] = 6,
        ["_MiniTalk"] = 3,
    };

    /// <summary>
    ///     Which value carries the text, for addons where it is not index 0.
    /// </summary>
    /// <remarks>
    ///     Index 0 held for <c>Talk</c> and <c>TalkSubtitle</c>, which made it look like a convention.
    ///     It is not: <c>_ScreenInfoFront</c>, the banner that narrates variant dungeons, puts its line
    ///     at index 3 of ten. Found by <c>/gubal find</c> rather than by reading, because nothing in
    ///     the struct definitions says so — the addon is not in FFXIVClientStructs at all.
    /// </remarks>
    private static readonly Dictionary<string, int> BodyValue = new(StringComparer.Ordinal)
    {
        ["_ScreenInfoFront"] = 3,
    };

    private readonly string[] addonNames;
    private readonly Configuration config;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly MissLog misses;
    private readonly MacroResolver resolver;
    private readonly TranslationStore store;

    private readonly IAddonLifecycle.AddonEventDelegate onPreDraw;
    private readonly IAddonLifecycle.AddonEventDelegate onPreRefresh;

    private readonly Dictionary<string, int> inspections = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenAddons = new(StringComparer.Ordinal);

    /// <summary>What our own output reads back as on the value route, per addon.</summary>
    private readonly Dictionary<string, string> valueInjected = new(StringComparer.Ordinal);

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

    public OverlayHandler(
        IAddonLifecycle lifecycle,
        IPluginLog log,
        Configuration config,
        TranslationStore store,
        MissLog misses,
        MacroResolver resolver,
        params string[] addonNames)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        this.config = config;
        this.store = store;
        this.misses = misses;
        this.resolver = resolver;
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

        var index = BodyValue.GetValueOrDefault(name, 0);
        if (index >= count)
        {
            return;
        }

        var text = AtkText.ReadString(values[index]);
        if (string.IsNullOrWhiteSpace(text) || text == this.valueInjected.GetValueOrDefault(name, string.Empty))
        {
            return;
        }

        if (!this.TryTranslate(text, name, EventContext.ActiveQuestConversation(), out var translated))
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
        this.valueInjected[name] = AtkText.ReadString(values[index]);
        this.InjectedCount++;
    }

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.config.Enabled)
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
        }

        var known = BodyNode.TryGetValue(name, out var bodyId);

        // Resolved once for the whole sweep rather than once per node. It walks every event handler
        // the client has loaded — 629 in a typical session — and the answer cannot change between
        // two nodes of the same addon in the same frame, so asking twice was only ever waste. This is
        // most of what the per-node guard below was protecting against.
        var conversation = EventContext.ActiveQuestConversation();

        foreach (var pointer in this.nodeBuffer)
        {
            var node = (AtkTextNode*)pointer;
            var id = node->NodeId;

            // A characterised addon gets exactly its body node; an unknown one gets every node, so
            // the miss log reveals its layout. That is how both known ids here were established.
            if (known && id != bodyId)
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

            // The node id goes into the miss record, not just the addon name. Without it the log
            // cannot distinguish the body from the speaker: a _BattleTalk pass recorded the NPC name
            // "Y'nazqha" as a missed line alongside two real ones, and there was no way to tell which
            // node each came from. Learn the layout from the log, then narrow this scan to the body.
            if (!this.TryTranslate(onScreen, $"{name}#{id}", conversation, out var translated))
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
            var probing = this.config.ProbeEvents && isMiniTalk;
            if (probing)
            {
                AddonInspector.DumpMiniTalk(this.log, addon, "before inject");
            }

            SeStringWriter.Write(node, resolved);

            // Balloons only. Everything else this handler writes to is a fixed-size overlay that
            // already arrives with WordWrap set.
            if (isMiniTalk)
            {
                BalloonLayout.Fit(addon, node);
            }

            // After the refit, so it is the geometry actually on screen.
            if (probing)
            {
                AddonInspector.DumpMiniTalk(this.log, addon, "after inject");
            }

            this.nodeInjected[pointer] = AtkText.ReadNodeText(node);
            this.InjectedCount++;
        }
    }

    private bool TryTranslate(string text, string addonName, string? conversation, out string translated)
    {
        var key = TextKey.Normalize(text);

        if (this.store.TryGetTranslation(conversation, key, out translated))
        {
            return true;
        }

        if (this.config.LogMisses)
        {
            this.misses.Record(key, text, addonName);
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
