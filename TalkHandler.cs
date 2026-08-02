using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

/// <summary>
///     Injects pre-translated dialogue into the <c>Talk</c> addon.
/// </summary>
/// <remarks>
///     <para>
///         The approach is Echoglossian's, minus everything asynchronous. The important idea: don't
///         fight the UI. On <see cref="AddonEvent.PreRefresh" /> we swap the addon's <em>source</em>
///         values before the game populates its text nodes, so the game does its own word wrapping and
///         plays its own typewriter reveal animation from our Spanish string. Both come free.
///     </para>
///     <para>
///         The <see cref="AddonEvent.PreDraw" /> pass is a safety net for cases where the game
///         repopulates the node from a source we didn't intercept. It is guarded by a string compare,
///         so a real write happens roughly once per dialogue line and every other frame is a cheap
///         read.
///     </para>
/// </remarks>
internal sealed unsafe class TalkHandler : IDisposable
{
    private const string AddonName = "Talk";

    // Node ids for the Talk addon. Node 10 is the wrap-width authority for the text node.
    private const int NameNodeId = 2;
    private const int TextNodeId = 3;
    private const int ParentNodeId = 10;

    /// <summary>How many lines the event probe reports before going quiet.</summary>
    private const int MaxProbeDumps = 20;

    private readonly Configuration config;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly ISeStringEvaluator evaluator;
    private readonly MissLog misses;
    private readonly TranslationStore store;

    // Cached delegate instances. RegisterListener(evt, name, this.Method) allocates a NEW delegate on
    // every call, so unregistering with a fresh method group leaves the listener attached — a leak that
    // shows up as duplicate work after a dev reload. Echoglossian solves this with a
    // ConditionalWeakTable; for three listeners, fields are enough.
    private readonly IAddonLifecycle.AddonEventDelegate onPreDraw;
    private readonly IAddonLifecycle.AddonEventDelegate onPreFinalize;
    private readonly IAddonLifecycle.AddonEventDelegate onPreHide;
    private readonly IAddonLifecycle.AddonEventDelegate onPreRefresh;

    // Current line. sourceText is what we looked up; injectedText is what we wrote.
    private string sourceText = string.Empty;
    private string sourceName = string.Empty;
    private string injectedText = string.Empty;
    private string injectedName = string.Empty;

    // Node presentation state, captured before our first write so it can be put back.
    private bool nodeStateCaptured;
    private TextFlags originalTextFlags;
    private byte originalFontSize;
    private ushort originalWidth;

    // Last known addon pointer, so Dispose can restore a line that is still on screen. Cleared on
    // PreFinalize, which is the point at which the nodes stop being valid.
    private nint lastAddon;

    private int probeCount;
    private string lastProbedText = string.Empty;

    public TalkHandler(
        IAddonLifecycle lifecycle,
        IPluginLog log,
        Configuration config,
        TranslationStore store,
        MissLog misses,
        ISeStringEvaluator evaluator)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        this.config = config;
        this.store = store;
        this.misses = misses;
        this.evaluator = evaluator;

        this.onPreRefresh = this.OnPreRefresh;
        this.onPreDraw = this.OnPreDraw;
        this.onPreHide = this.OnPreHide;
        this.onPreFinalize = this.OnPreFinalize;

        this.lifecycle.RegisterListener(AddonEvent.PreRefresh, AddonName, this.onPreRefresh);
        this.lifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, this.onPreDraw);
        this.lifecycle.RegisterListener(AddonEvent.PreHide, AddonName, this.onPreHide);
        this.lifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, this.onPreFinalize);

        this.log.Information("TalkHandler registered on addon '{Addon}'.", AddonName);
    }

    /// <summary>Number of lines successfully injected since load. Sanity metric for /gubal status.</summary>
    public int InjectedCount { get; private set; }

    public void Dispose()
    {
        this.lifecycle.UnregisterListener(AddonEvent.PreRefresh, AddonName, this.onPreRefresh);
        this.lifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, this.onPreDraw);
        this.lifecycle.UnregisterListener(AddonEvent.PreHide, AddonName, this.onPreHide);
        this.lifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, this.onPreFinalize);

        // Put the English back if our text is still on screen, so unloading mid-conversation doesn't
        // leave an orphaned Spanish line.
        this.RestoreNode(this.lastAddon);
        this.ClearLine();
    }

    /// <summary>
    ///     Primary injection. Swaps the addon's source values before the game builds its text nodes.
    /// </summary>
    private void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.config.Enabled || args is not AddonRefreshArgs refresh)
        {
            return;
        }

        this.lastAddon = args.Addon.Address;

        var values = (AtkValue*)refresh.AtkValues;
        var count = (int)refresh.AtkValueCount;
        if (values is null || count < 2)
        {
            return;
        }

        var text = AtkText.ReadString(values[0]); // [0] = dialogue body
        var name = AtkText.ReadString(values[1]); // [1] = speaker name

        // Because we mutate the AtkValues in place, a later refresh can hand our own Spanish output
        // back as if it were fresh source text. Without this remap we'd look up Spanish in a
        // Spanish-keyed dictionary, miss, clear state, and revert the line.
        if (this.injectedText.Length > 0 && text == this.injectedText)
        {
            text = this.sourceText;
            name = this.sourceName;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Deliberately before the lookup: the probe is about where the line came from, which is
        // interesting whether or not we can translate it.
        this.MaybeProbeEvents(text);

        // Clock-dependent greetings drift out of their key as the Eorzean hour advances. Throttled
        // internally, so this is a cheap no-op on almost every line.
        this.store.RefreshTimeSensitive();

        var key = TextKey.Normalize(text);

        // Scopes the lookup to the quest being played, so identical English in another quest does not
        // shadow this line's own translation. Null outside a quest scene, which falls back to text.
        var conversation = EventContext.ActiveQuestConversation();

        if (!this.store.TryGetTranslation(conversation, key, out var translated))
        {
            if (this.config.LogMisses)
            {
                this.misses.Record(key, text, name);
            }

            this.ClearLine();
            return;
        }

        this.sourceText = text;
        this.sourceName = name;
        this.injectedText = this.ResolveValue(translated);
        this.injectedName = this.config.TranslateNpcNames && this.store.TryGetNpcName(name, out var esName)
            ? esName
            : string.Empty;

        // SetManagedString allocates through the game's allocator and takes ownership, so there is no
        // buffer lifetime for us to manage.
        values[0].SetManagedString(this.injectedText);
        if (this.injectedName.Length > 0)
        {
            values[1].SetManagedString(this.injectedName);
        }

        this.InjectedCount++;
        this.log.Debug(
            "Injected line from '{Speaker}' ({Chars} chars, conversation={Conversation}).",
            name,
            this.injectedText.Length,
            conversation ?? "(none)");
    }

    /// <summary>
    ///     Idempotent re-assert. Fires every frame; the string compare means it almost always returns
    ///     immediately.
    /// </summary>
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

        // Nothing of ours on screen: if we changed this node's presentation for a previous line, put
        // it back. Without this the width we forced survives into the next untranslated line, which
        // then wraps after two or three words in an otherwise full-width box.
        if (this.injectedText.Length == 0)
        {
            this.RestorePresentation(addon);
            return;
        }

        this.lastAddon = args.Addon.Address;

        var textNode = addon->GetTextNodeById(TextNodeId);
        var parentNode = addon->GetNodeById(ParentNodeId);
        if (textNode is null || parentNode is null || textNode->NodeText.IsEmpty)
        {
            return;
        }

        if (this.injectedName.Length > 0)
        {
            var nameNode = addon->GetTextNodeById(NameNodeId);
            if (nameNode is not null && AtkText.ReadNodeText(nameNode) != this.injectedName)
            {
                nameNode->SetText(this.injectedName);
            }
        }

        // The whole flicker defence: if the node already says what we want, do nothing.
        if (AtkText.ReadNodeText(textNode) == this.injectedText)
        {
            return;
        }

        this.CaptureNodeState(textNode);

        var wrapWidth = parentNode->GetWidth();

        textNode->TextFlags = TextFlags.WordWrap | TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;
        textNode->FontSize = FontSizeFor(this.injectedText.Length);
        textNode->SetWidth(wrapWidth);
        textNode->SetText(this.injectedText);
        textNode->ResizeNodeForCurrentText();

        // AutoAdjustNodeSize resizes both axes, so the call above can shrink the width we just set —
        // and a narrowed node stays narrow for whatever renders next. Force it back.
        if (textNode->GetWidth() != wrapWidth)
        {
            textNode->SetWidth(wrapWidth);
        }
    }

    /// <summary>
    ///     Puts back the text node's original flags, font size and width.
    /// </summary>
    /// <remarks>
    ///     Only the presentation, never the text: by the time this runs the game has usually written
    ///     its own line into the node, and overwriting that would be worse than the layout glitch this
    ///     fixes.
    /// </remarks>
    private void RestorePresentation(AtkUnitBase* addon)
    {
        if (!this.nodeStateCaptured)
        {
            return;
        }

        this.nodeStateCaptured = false;

        var textNode = addon->GetTextNodeById(TextNodeId);
        if (textNode is null)
        {
            return;
        }

        textNode->TextFlags = this.originalTextFlags;
        textNode->FontSize = this.originalFontSize;
        textNode->SetWidth(this.originalWidth);
        textNode->ResizeNodeForCurrentText();
    }

    private void OnPreHide(AddonEvent type, AddonArgs args)
    {
        this.RestoreNode(args.Addon);
        this.ClearLine();
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        // Don't touch nodes here — they are being torn down. Drop the captured state too: the node it
        // describes no longer exists, and a later restore would write through a dangling pointer.
        this.lastAddon = nint.Zero;
        this.nodeStateCaptured = false;
        this.ClearLine();
    }

    /// <summary>
    ///     Resolves a translated value that carries game macro syntax.
    /// </summary>
    /// <remarks>
    ///     Translations keep the game's own macro syntax — <c>&lt;if(gnum4,cansada,cansado)&gt;</c> —
    ///     rather than a bespoke token format, so a single evaluator serves both the key and the value
    ///     and there is no second syntax to define or keep in sync.
    ///     <para>
    ///         Resolved here rather than at load so the result reflects state at the moment of display,
    ///         and because a translation that fails to evaluate degrades to showing its raw macro
    ///         instead of breaking the line entirely.
    ///     </para>
    /// </remarks>
    private string ResolveValue(string value)
    {
        // Most translations have no macro at all; skip the call rather than pay for it per line.
        if (!value.Contains('<', StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var resolved = this.evaluator.EvaluateMacroString(value).ExtractText();
            return string.IsNullOrWhiteSpace(resolved) ? value : resolved;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Could not evaluate translated value; injecting it verbatim: {Value}", value);
            return value;
        }
    }

    /// <summary>
    ///     Spanish runs roughly 20% longer than English; shrink the font rather than let the box clip.
    ///     Ladder taken from Echoglossian.
    /// </summary>
    private static byte FontSizeFor(int length)
    {
        return length switch
        {
            >= 350 => 11,
            >= 256 => 12,
            _ => 14,
        };
    }

    /// <summary>
    ///     Logs the live event-handler state once per new line, up to a cap.
    /// </summary>
    /// <remarks>
    ///     Settles design questions by observation rather than assumption. It has already answered
    ///     two — the addon carries no text id, and the live quest handler does name the conversation.
    ///     The open one is cutscenes: their corpus conversation is <c>cut_scene/…</c> while the
    ///     handler reports <c>quest/…</c>, so those lines should fall back to the text index, and
    ///     that needs confirming in game rather than reasoning about.
    /// </remarks>
    private void MaybeProbeEvents(string text)
    {
        if (!this.config.ProbeEvents
            || this.probeCount >= MaxProbeDumps
            || text == this.lastProbedText)
        {
            return;
        }

        this.lastProbedText = text;
        this.probeCount++;

        this.log.Information("[probe] line {Index}/{Max}: {Text}", this.probeCount, MaxProbeDumps, text);
        EventProbe.Dump(this.log, text);

        if (this.probeCount == MaxProbeDumps)
        {
            this.log.Information("[probe] cap reached; toggle with /gubal probe.");
        }
    }

    private void CaptureNodeState(AtkTextNode* node)
    {
        if (this.nodeStateCaptured || node is null)
        {
            return;
        }

        this.originalTextFlags = node->TextFlags;
        this.originalFontSize = node->FontSize;
        this.originalWidth = node->GetWidth();
        this.nodeStateCaptured = true;
    }

    /// <summary>
    ///     Restores the English text and the node's original presentation, but only if the node still
    ///     contains exactly what we wrote.
    /// </summary>
    /// <remarks>
    ///     Ownership rule: never stomp text the game or another plugin has changed since our write.
    /// </remarks>
    private void RestoreNode(nint addonPointer)
    {
        if (!this.nodeStateCaptured
            || addonPointer == nint.Zero
            || this.injectedText.Length == 0
            || this.sourceText.Length == 0)
        {
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)addonPointer;
            if (addon is null)
            {
                return;
            }

            var textNode = addon->GetTextNodeById(TextNodeId);
            if (textNode is null || AtkText.ReadNodeText(textNode) != this.injectedText)
            {
                return;
            }

            textNode->TextFlags = this.originalTextFlags;
            textNode->FontSize = this.originalFontSize;
            textNode->SetWidth(this.originalWidth);
            textNode->SetText(this.sourceText);
            textNode->ResizeNodeForCurrentText();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to restore original Talk text.");
        }
    }

    /// <summary>
    ///     Drops the current line's text state.
    /// </summary>
    /// <remarks>
    ///     Deliberately leaves <c>nodeStateCaptured</c> set. The captured flags and width are still
    ///     needed: the next PreDraw uses them to undo the presentation changes. Clearing the flag here
    ///     — which an earlier version did — threw the original values away on every miss, so the
    ///     narrowed node was never restored.
    /// </remarks>
    private void ClearLine()
    {
        this.sourceText = string.Empty;
        this.sourceName = string.Empty;
        this.injectedText = string.Empty;
        this.injectedName = string.Empty;
    }
}
