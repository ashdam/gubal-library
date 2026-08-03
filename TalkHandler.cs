using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

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
    private readonly MacroResolver resolver;
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

    // Current line. sourceText is what we looked up; injectedSeString is what we wrote.
    private string sourceText = string.Empty;
    private string sourceName = string.Empty;
    private string injectedName = string.Empty;

    /// <summary>The bytes we injected, kept so PreDraw can re-assert exactly the same line.</summary>
    private ReadOnlySeString injectedSeString;

    /// <summary>The injected line with payloads stripped — for the font ladder and the log.</summary>
    private string injectedPlain = string.Empty;

    /// <summary>
    ///     The game's own line, in bytes, for handing back in <see cref="RestoreNode" />.
    /// </summary>
    /// <remarks>
    ///     Not <see cref="sourceText" />, which comes from <c>AtkText.ReadString</c> and therefore
    ///     carries literal asterisks wherever the line was italicised. Writing that back is a visible
    ///     defect; writing these bytes back is lossless.
    /// </remarks>
    private byte[] sourceBytes = [];

    // What our own output reads back as. Both guards below compare a string read out of the game
    // against one of these, so each has to be captured from the same place it will later be read
    // from: the value for PreRefresh's re-entry guard, the node for PreDraw's flicker guard.
    //
    // Captured after writing rather than derived from what we meant to write. That is what makes the
    // comparison immune to how Dalamud chooses to render a payload — asterisks for italics today,
    // whatever it decides for the next payload type we meet. Deriving it cost a whole class of bug:
    // the read-back never matched, so the plugin took its own Spanish for fresh source text, failed
    // to find it, and reverted the line it had just translated.
    private string injectedValueText = string.Empty;
    private string injectedNodeText = string.Empty;

    // Node presentation state, captured before our first write so it can be put back.
    private bool nodeStateCaptured;
    private TextFlags originalTextFlags;
    private byte originalFontSize;
    private ushort originalWidth;

    /// <summary>Which line the snapshot above was taken for — see <see cref="CaptureNodeState" />.</summary>
    private string capturedForSourceText = string.Empty;

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
        MacroResolver resolver)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        this.config = config;
        this.store = store;
        this.misses = misses;
        this.resolver = resolver;

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
        var reentered = this.injectedValueText.Length > 0 && text == this.injectedValueText;
        if (reentered)
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

        // Before anything of ours runs, so it reports the state this line ARRIVED in — which is the
        // state that decides how the game wraps it.
        this.LogNodeGeometry((AtkUnitBase*)args.Addon.Address, "line arrives");

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

            // Give the node its width back HERE, before the game lays this line out — not in
            // PreDraw, which is where it used to happen and which is a frame too late.
            //
            // The order is the whole bug. A translated line forces the text node narrow so the
            // Spanish wraps inside the box; the next untranslated line arrives, the game writes its
            // own English into that still-narrow node and picks its line breaks against the narrow
            // width, and only then does PreDraw hand the width back. The breaks are already baked
            // in by that point — restoring the width does not re-flow text that has been laid out.
            //
            // On screen: English wrapping after two or three words in a box that is visibly full
            // width. It was reported, patched once in PreDraw, and looked fixed because the width
            // really was being restored. It was just being restored after it mattered.
            this.RestoreNode(args.Addon.Address, restoreText: false);
            this.LogNodeGeometry((AtkUnitBase*)args.Addon.Address, "after restore (untranslated)");

            this.ClearLine();
            return;
        }

        // A translation exists, which is not the same as a translation we can use. If its macros will
        // not evaluate this line is a miss like any other — and it takes the miss path exactly, width
        // restore included, because skipping that is what leaves the next English line wrapping after
        // three words in a full-width box.
        if (!this.resolver.TryResolve(translated, out var resolved, out var plain))
        {
            this.RestoreNode(args.Addon.Address, restoreText: false);
            this.ClearLine();
            return;
        }

        if (!reentered)
        {
            // Read before we overwrite it, and only while it still holds the game's own line. On a
            // re-entry this value carries our Spanish, and capturing that would turn "restore" into a
            // no-op that leaves Spanish on screen after the plugin is switched off.
            this.sourceBytes = AtkText.ReadBytes(values[0]);
        }

        this.sourceText = text;
        this.sourceName = name;
        this.injectedSeString = resolved;
        this.injectedPlain = plain;
        this.injectedName = this.config.TranslateNpcNames && this.store.TryGetNpcName(name, out var esName)
            ? esName
            : string.Empty;

        SeStringWriter.Write(&values[0], resolved);
        if (this.injectedName.Length > 0)
        {
            // Still a string: NPC names carry no macros, so nothing is lost, and the name node is
            // compared as a string too.
            values[1].SetManagedString(this.injectedName);
        }

        // Now that it is in place, record what it reads back as. See the field comments: this is the
        // only form the guards can safely compare against.
        this.injectedValueText = AtkText.ReadString(values[0]);
        this.injectedNodeText = this.injectedValueText;

        this.InjectedCount++;
        this.log.Debug(
            "Injected line from '{Speaker}' ({Chars} chars, {Bytes} bytes, conversation={Conversation}).",
            name,
            this.injectedPlain.Length,
            resolved.ByteLength,
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
        if (this.injectedSeString.ByteLength == 0)
        {
            this.RestoreNode(args.Addon.Address);
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
        if (AtkText.ReadNodeText(textNode) == this.injectedNodeText)
        {
            return;
        }

        this.CaptureNodeState(textNode, this.sourceText);

        var wrapWidth = parentNode->GetWidth();

        textNode->TextFlags = TextFlags.WordWrap | TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;

        // The character count, not the byte count. Every <italic> pair adds ten bytes and no
        // characters, so sizing by bytes drops the font a step on lines that did not need it.
        textNode->FontSize = FontSizeFor(this.injectedPlain.Length);
        textNode->SetWidth(wrapWidth);
        SeStringWriter.Write(textNode, this.injectedSeString);

        // What the node reads back as may not be what the value read back as — the game repopulates
        // it — so re-anchor the guard on the node's own rendering, or this runs again next frame.
        this.injectedNodeText = AtkText.ReadNodeText(textNode);

        textNode->ResizeNodeForCurrentText();

        // AutoAdjustNodeSize resizes both axes, so the call above can shrink the width we just set —
        // and a narrowed node stays narrow for whatever renders next. Force it back.
        if (textNode->GetWidth() != wrapWidth)
        {
            textNode->SetWidth(wrapWidth);
        }
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
        this.capturedForSourceText = string.Empty;
        this.ClearLine();
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
    /// <summary>
    ///     Logs the text node's geometry, so a wrapping complaint can be measured instead of argued.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Added after a long round of reasoning about whether the plugin was leaving the Talk
    ///         node narrow. Every argument on both sides was plausible and none was checkable: the
    ///         numbers that would settle it — this node's width against its parent's — live in game
    ///         memory and nothing was reporting them.
    ///     </para>
    ///     <para>
    ///         Note that <c>/gubal off</c> does not prove the plugin innocent of a layout complaint.
    ///         Disabling it stops the restore as well as the writes, so a node left narrow earlier in
    ///         the session simply stays that way. Only these numbers distinguish "we never touched
    ///         it" from "we touched it and never put it back".
    ///     </para>
    /// </remarks>
    private void LogNodeGeometry(AtkUnitBase* addon, string when)
    {
        if (!this.config.ProbeEvents || addon is null)
        {
            return;
        }

        var textNode = addon->GetTextNodeById(TextNodeId);
        var parentNode = addon->GetNodeById(ParentNodeId);
        if (textNode is null || parentNode is null)
        {
            return;
        }

        this.log.Information(
            "[geometry] {When}: text node w={Width} h={Height} font={Font} flags={Flags} | "
            + "parent w={ParentWidth} | captured={Captured} originalW={OriginalWidth}",
            when,
            textNode->GetWidth(),
            textNode->GetHeight(),
            textNode->FontSize,
            textNode->TextFlags,
            parentNode->GetWidth(),
            this.nodeStateCaptured,
            this.nodeStateCaptured ? this.originalWidth : (ushort)0);
    }

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

    /// <summary>
    ///     Snapshots the node's presentation before the first write for a given line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keyed by the line it describes, not by a bare "have I captured yet" flag. With a bare
    ///         flag the snapshot could end up describing our own handiwork: clear it while the node
    ///         is still narrowed — which <c>PreFinalize</c> does — and the next capture records the
    ///         narrowed width as though it were the game's. Every restore after that returns the node
    ///         to a width it never had, and it stays that way until the addon is rebuilt from
    ///         scratch. A defect that survives the thing that was supposed to end it.
    ///     </para>
    ///     <para>
    ///         Echoglossian keys its own snapshot the same way, on the source text it was taken for.
    ///     </para>
    /// </remarks>
    private void CaptureNodeState(AtkTextNode* node, string forSourceText)
    {
        if (node is null || string.IsNullOrEmpty(forSourceText))
        {
            return;
        }

        if (this.nodeStateCaptured && this.capturedForSourceText == forSourceText)
        {
            return;
        }

        this.originalTextFlags = node->TextFlags;
        this.originalFontSize = node->FontSize;
        this.originalWidth = node->GetWidth();
        this.nodeStateCaptured = true;
        this.capturedForSourceText = forSourceText;
    }

    /// <summary>
    ///     Hands the node back: its presentation always, its English text only if ours is still there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two restores, deliberately not one, because they answer to different rules.
    ///     </para>
    ///     <para>
    ///         <b>Presentation is ours.</b> We narrowed the node, changed its font size and replaced
    ///         its flags; nobody else did. Handing that back is unconditional, and making it
    ///         conditional was the defect: the single guard below used to cover both restores, so the
    ///         moment the game had written its own line — which it usually has by the time this runs
    ///         — we returned without widening the node. It stayed narrow, and the next capture
    ///         recorded that width as if it were the game's.
    ///     </para>
    ///     <para>
    ///         <b>Text is the game's.</b> Only put the English back if the node still holds exactly
    ///         what we wrote. Anything else means the game or another plugin has moved on, and
    ///         overwriting that would be worse than any layout glitch.
    ///     </para>
    ///     <para>
    ///         Echoglossian draws the same line, taking <c>restoreText</c> as a parameter separate
    ///         from the geometry it always restores.
    ///     </para>
    /// </remarks>
    /// <param name="restoreText">
    ///     False where the game is about to write its own line anyway — the miss path in
    ///     <c>PreRefresh</c>. Putting the previous line's English back there would only be correct
    ///     because something else overwrites it a moment later, and depending on that is how the
    ///     original defect happened. Echoglossian takes the same flag for the same reason.
    /// </param>
    private void RestoreNode(nint addonPointer, bool restoreText = true)
    {
        if (!this.nodeStateCaptured || addonPointer == nint.Zero)
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
            if (textNode is null)
            {
                return;
            }

            var oursIsStillOnScreen = restoreText
                                      && this.injectedSeString.ByteLength > 0
                                      && this.sourceBytes.Length > 0
                                      && AtkText.ReadNodeText(textNode) == this.injectedNodeText;

            textNode->TextFlags = this.originalTextFlags;
            textNode->FontSize = this.originalFontSize;
            textNode->SetWidth(this.originalWidth);

            // Both branches go through bytes, and both used to go through strings. Handing back the
            // English via AtkText.ReadString's rendering wrote a literal "*Orion*" over a line the
            // game had italicised properly — the same string round-trip that loses formatting on the
            // way in, materialising it as visible junk on the way out.
            if (oursIsStillOnScreen)
            {
                SeStringWriter.Write(textNode, this.sourceBytes);
            }
            else
            {
                // Re-apply what is already there. Restoring a width does not re-flow text that has
                // been laid out, so without this the node ends up the right width still carrying
                // the breaks it was given at the wrong one.
                SeStringWriter.Write(textNode, AtkText.ReadNodeBytes(textNode));
            }

            textNode->ResizeNodeForCurrentText();

            // The snapshot described a node that no longer exists in that state. Keeping it would let
            // it be restored a second time over a line it was never taken for.
            this.nodeStateCaptured = false;
            this.capturedForSourceText = string.Empty;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to restore the Talk node.");
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
        this.sourceBytes = [];
        this.injectedSeString = default;
        this.injectedPlain = string.Empty;
        this.injectedName = string.Empty;
        this.injectedValueText = string.Empty;
        this.injectedNodeText = string.Empty;
    }
}
