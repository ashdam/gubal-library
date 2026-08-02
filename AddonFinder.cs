using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Answers "which addon draws this line?" by listening to every addon at once and reporting the
///     ones whose values contain a given substring.
/// </summary>
/// <remarks>
///     <para>
///         The runtime counterpart to the extractor's <c>--scanall</c>, and needed for the same
///         reason. That one answers "which sheet holds this text?" when a line turns out to be
///         missing from the corpus; this answers "which addon puts it on screen?" when a line is in
///         the corpus and still shows in English.
///     </para>
///     <para>
///         Built when narration split across two surfaces. "And so does our ship cast off from the
///         docks." arrived through <c>TalkSubtitle</c> and translated; the sentence immediately after
///         it, from the same narrator in the same cutscene, never reached that addon at all — and
///         therefore never reached any miss log either. Guessing which addon owns it from a list of
///         candidates is exactly the kind of assumption that has cost time on this project already.
///     </para>
///     <para>
///         Registered without an addon name, so it sees every refresh in the game. That would be
///         far too noisy to log wholesale, which is why it filters on the needle and stays silent
///         otherwise — the point is to catch one string, not to trace the UI.
///     </para>
/// </remarks>
internal sealed unsafe class AddonFinder : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly IAddonLifecycle.AddonEventDelegate onPreRefresh;
    private readonly IAddonLifecycle.AddonEventDelegate onPreDraw;

    private readonly HashSet<string> reported = new(StringComparer.Ordinal);

    /// <summary>Reused across sweeps so the scan does not allocate a list per frame.</summary>
    private readonly List<nint> nodeBuffer = [];

    /// <summary>
    ///     Frames between node sweeps.
    /// </summary>
    /// <remarks>
    ///     <c>PreDraw</c> fires for every visible addon every frame, and reading a node's text
    ///     allocates a managed string, so sweeping all of them unthrottled is far too expensive to
    ///     leave switched on. Sampling twice a second is ample: anything a player can read stays up
    ///     for seconds.
    /// </remarks>
    private const int SweepEveryFrames = 30;

    private int frame;

    public AddonFinder(IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        this.onPreRefresh = this.OnPreRefresh;
        this.onPreDraw = this.OnPreDraw;
    }

    /// <summary>The substring to hunt for. Empty means the finder is off.</summary>
    public string Needle { get; private set; } = string.Empty;

    public bool Active => this.Needle.Length > 0;

    /// <summary>Starts hunting for <paramref name="needle" />, or stops if it is empty.</summary>
    public void Hunt(string needle)
    {
        var wasActive = this.Active;
        this.Needle = needle.Trim();
        this.reported.Clear();

        if (this.Active && !wasActive)
        {
            // Both routes, for the same reason the handlers need both: an addon that writes straight
            // into its nodes never refreshes, and a finder watching only PreRefresh is blind to
            // exactly the addons it was built to discover. That is not hypothetical — this tool
            // failed to find a speech balloon for precisely that reason.
            this.lifecycle.RegisterListener(AddonEvent.PreRefresh, this.onPreRefresh);
            this.lifecycle.RegisterListener(AddonEvent.PreDraw, this.onPreDraw);
        }
        else if (!this.Active && wasActive)
        {
            this.lifecycle.UnregisterListener(AddonEvent.PreRefresh, this.onPreRefresh);
            this.lifecycle.UnregisterListener(AddonEvent.PreDraw, this.onPreDraw);
            this.log.Information("[find] stopped.");
            return;
        }

        // Logged on every call, not just on activation. Changing the needle while already hunting
        // used to be silent, which reads as "the command did nothing".
        if (this.Active)
        {
            this.log.Information("[find] watching every addon, values and nodes, for: {Needle}", this.Needle);
        }
    }

    public void Dispose()
    {
        if (this.Active)
        {
            this.lifecycle.UnregisterListener(AddonEvent.PreRefresh, this.onPreRefresh);
            this.lifecycle.UnregisterListener(AddonEvent.PreDraw, this.onPreDraw);
        }
    }

    /// <summary>
    ///     Sweeps visible addons' text nodes, catching text that never arrives as a value.
    /// </summary>
    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.Active || ++this.frame % SweepEveryFrames != 0)
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon is null || !addon->IsVisible)
        {
            return;
        }

        // The same walk the overlay handler uses, deliberately. This class carried its own copy of it,
        // with its own literal depth limit and component-type floor. A finder that reaches a different
        // set of nodes than the handlers do is worse than no finder: it reports a line as living
        // somewhere the code that has to translate it will never look.
        AddonNodes.CollectTextNodes(addon, this.nodeBuffer);

        foreach (var pointer in this.nodeBuffer)
        {
            var node = (AtkTextNode*)pointer;
            var text = AtkText.ReadNodeText(node);
            if (text.Length == 0 || !text.Contains(this.Needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // One report per addon and node. An addon that redraws every frame would otherwise bury
            // the answer under thousands of identical lines.
            if (!this.reported.Add($"{args.AddonName}!node{node->NodeId}"))
            {
                continue;
            }

            this.log.Information(
                "[find] *** '{Addon}' NODE {Id} (written direct, no value): {Text}",
                args.AddonName,
                node->NodeId,
                text);
        }
    }

    private void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.Active || args is not AddonRefreshArgs refresh)
        {
            return;
        }

        var values = (AtkValue*)refresh.AtkValues;
        var count = (int)refresh.AtkValueCount;
        if (values is null)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var value = values[i];
            var text = AtkText.ReadString(value);
            if (text.Length == 0 || !text.Contains(this.Needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // One report per addon and index. An addon that refreshes every frame would otherwise
            // bury the answer under thousands of identical lines.
            var seen = $"{args.AddonName}#{i}";
            if (!this.reported.Add(seen))
            {
                continue;
            }

            this.log.Information(
                "[find] *** '{Addon}' value [{Index}] of {Count}: {Text}",
                args.AddonName,
                i,
                count,
                text);
        }
    }
}
