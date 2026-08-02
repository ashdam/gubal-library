using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace GubalLibrary;

/// <summary>
///     Names the conversation the player is currently in, so a lookup can be scoped to it.
/// </summary>
/// <remarks>
///     <para>
///         The <c>Talk</c> addon hands over a rendered string and nothing else, which is why the
///         lookup joins on text. But the addon is the last step, not the only source: the quest is
///         driven by a <see cref="QuestEventHandler" />, and that handler knows its own
///         <c>ScriptPath</c> — <c>047/SubWil901_04779</c>, which is the corpus's
///         <c>quest/047/SubWil901_04779</c> minus the prefix.
///     </para>
///     <para>
///         That one string is worth a great deal. Of the 5,378 English dialogue lines that repeat
///         somewhere in the corpus, 4,407 repeat across <em>different</em> conversations, and 4,078 of
///         those are character dialogue. Scoping the key by conversation lets each of them carry its
///         own Spanish; without it they all collapse onto whichever translation loaded first.
///     </para>
///     <para>
///         The row id is reachable too — <c>QuestEventHandler.LuaTexts</c> maps row to
///         <c>{ Key, Value }</c>, with <c>Key</c> being the corpus's <c>TEXT_..._000_001</c> token —
///         but it is not worth taking. Finding the row means scanning a few hundred
///         <c>Utf8String</c>s per line, and it only buys the 971 repeats confined to a single
///         conversation, which are one script line reached by two branches and ought to read the same
///         anyway.
///     </para>
/// </remarks>
internal static unsafe class EventContext
{
    /// <summary>A handler that is not running a scene reports this.</summary>
    private const short NoScene = -1;

    /// <summary>
    ///     The active quest's conversation id, or <c>null</c> when no quest scene is running.
    /// </summary>
    /// <remarks>
    ///     Null is the normal case, not a failure: ambient chatter, shops and most incidental talk run
    ///     no quest scene, and those lines fall back to the text-only index.
    /// </remarks>
    public static string? ActiveQuestConversation()
    {
        try
        {
            var framework = EventFramework.Instance();
            if (framework is null)
            {
                return null;
            }

            // Every handler the client has loaded lives in this map — 629 of them in a typical session,
            // eight or more of which are quests. The discriminator is each handler's OWN Scene field.
            // EventFramework.Scene is a different, framework-wide value that reads -1 even while a
            // handler is mid-scene; reading that one instead is how this was previously, and wrongly,
            // concluded to be unreachable.
            foreach (var pair in framework->EventHandlerModule.EventHandlerMap)
            {
                var handler = pair.Item2.Value;
                if (handler is null || handler->Scene == NoScene)
                {
                    continue;
                }

                if (handler->Info.EventId.ContentId != EventHandlerContent.Quest)
                {
                    continue;
                }

                var script = ((QuestEventHandler*)handler)->ScriptPath.ToString();
                return string.IsNullOrEmpty(script) ? null : "quest/" + script;
            }
        }
        catch
        {
            // Reading through game pointers on the render path. A null lookup degrades to the text
            // index, which is the pre-existing behaviour; throwing here would break the line entirely.
        }

        return null;
    }
}
