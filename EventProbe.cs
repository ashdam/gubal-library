using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace GubalLibrary;

/// <summary>
///     Logs which event handler is driving the dialogue on screen, and what conversation it resolves
///     to.
/// </summary>
/// <remarks>
///     <para>
///         Built to settle whether the game's own identifier for a line is reachable while the
///         <c>Talk</c> window is open. It is: talking to Nananji matched 11 lines out of 11, with the
///         live <see cref="QuestEventHandler" /> reporting <c>ScriptPath=047/SubWil901_04779</c> —
///         the corpus's conversation id minus its <c>quest/</c> prefix.
///     </para>
///     <para>
///         An earlier probe answered "unreachable" and was wrong twice over. It read
///         <c>EventFramework.Scene</c>, the framework-wide field, when the discriminator is each
///         handler's own <see cref="EventHandler.Scene" />; and it ran from a chat command, so it
///         sampled after the dialogue had closed. Both readings agreed with each other and both
///         measured the wrong thing — which is why this one runs from inside <c>Talk</c>'s PreRefresh
///         and reports handlers individually.
///     </para>
///     <para>
///         It no longer scans <c>LuaTexts</c>. That map does yield the exact row key, but finding it
///         costs a few hundred string materialisations per line and only disambiguates repeats inside
///         a single conversation — which are one script line reached by two branches, and should read
///         the same regardless. The conversation alone separates 4,407 of the corpus's 5,378 repeats.
///     </para>
/// </remarks>
internal static unsafe class EventProbe
{
    /// <summary>A handler that is not running a scene reports this.</summary>
    private const short NoScene = -1;

    public static void Dump(IPluginLog log, string displayedText)
    {
        try
        {
            var framework = EventFramework.Instance();
            if (framework is null)
            {
                log.Information("[probe] EventFramework.Instance() is null.");
                return;
            }

            var handlers = framework->EventHandlerModule.EventHandlerMap;

            // The framework-wide Scene is logged only as a contrast: it reads -1 even while an owning
            // handler reports a live scene, which is exactly how this was previously misread.
            log.Information(
                "[probe] {Count} loaded handler(s); framework Scene={Scene}; resolved conversation={Conversation}",
                handlers.Count,
                framework->Scene,
                EventContext.ActiveQuestConversation() ?? "(none — will fall back to the text index)");

            var active = 0;

            foreach (var pair in handlers)
            {
                var handler = pair.Item2.Value;
                if (handler is null || handler->Scene == NoScene)
                {
                    continue;
                }

                active++;
                var id = handler->Info.EventId;

                log.Information(
                    "[probe]   ACTIVE content={Content} entry={Entry} id={Id:X8} scene={Scene}",
                    id.ContentId,
                    id.EntryId,
                    id.Id,
                    handler->Scene);

                if (id.ContentId != EventHandlerContent.Quest)
                {
                    continue;
                }

                var quest = (QuestEventHandler*)handler;
                log.Information(
                    "[probe]     QuestId={QuestId} ScriptPath='{Script}' LuaTexts={Rows} rows",
                    quest->QuestId,
                    quest->ScriptPath.ToString(),
                    quest->LuaTexts.Count);
            }

            if (active == 0)
            {
                // Not a fault. Ambient chatter, shops and incidental talk run no quest scene; those
                // lines are meant to reach the text-only index.
                log.Information("[probe]   no handler has a live scene: '{Text}'", displayedText);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[probe] Failed while reading event state.");
        }
    }
}
