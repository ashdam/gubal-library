using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
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
internal static unsafe partial class EventContext
{
    /// <summary>A handler that is not running a scene reports this.</summary>
    private const short NoScene = -1;

    /// <summary>
    ///     The scope to look a line up under, or <c>null</c> when the game offers none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two scopes, tried in that order, because a quest names one conversation while a
    ///         territory names a whole duty — the narrower answer is the better key wherever it exists.
    ///     </para>
    ///     <para>
    ///         The territory half exists because a dungeon boss has no quest handler, so every line it
    ///         speaks used to resolve on text alone. That is how Malphas came to say <em>¡Baila!</em>:
    ///         his "Dance!" and a FATE duellist's are the same English, the corpus holds a different
    ///         Spanish for each, and the text index keeps only the first it loaded.
    ///     </para>
    ///     <para>
    ///         Null is still the normal case — ambient chatter, shops and open-world talk have neither
    ///         — and those lines fall back to the text index exactly as before.
    ///     </para>
    /// </remarks>
    /// <param name="territory">
    ///     <c>IClientState.TerritoryType</c>, passed in rather than read here.
    /// </param>
    public static string? ActiveScope(uint territory, IPluginLog log)
    {
        // Not gated on "is this an instance". A pack only carries this scope for rows the game itself
        // places in a duty, so an open-world territory matches nothing and falls through — one
        // dictionary miss, against an extra API whose shape would be one more thing to be wrong about.
        return ActiveQuestConversation(log)
               ?? (territory == 0 ? null : "territory/" + territory);
    }

    /// <summary>
    ///     What a real <c>ScriptPath</c> looks like: <c>047/SubWil901_04779</c>.
    /// </summary>
    /// <remarks>
    ///     Measured, not assumed: of every quest conversation in the Spanish pack, the number that fail
    ///     this pattern is zero.
    /// </remarks>
    [GeneratedRegex(@"^[0-9]{3}/[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptPathShape();

    /// <summary>Warned once already; the condition is per-build, not per-line.</summary>
    private static bool shapeWarned;

    /// <summary>
    ///     The active quest's conversation id, or <c>null</c> when no quest scene is running.
    /// </summary>
    private static string? ActiveQuestConversation(IPluginLog log)
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
                if (string.IsNullOrEmpty(script))
                {
                    return null;
                }

                // A smoke detector for the one failure this file cannot otherwise announce. The cast
                // above reads a reverse-engineered struct layout, and a patch that moves ScriptPath's
                // offset does not throw — it returns whatever bytes sit there. The scope then matches
                // nothing, every quest line quietly falls back to the text index, and the 4,407 lines
                // that need scoping to tell them apart start showing another quest's Spanish. Nothing
                // on screen says so. Returning null changes none of that; it only makes it say so.
                //
                // Not a guarantee: junk that happens to look like NNN/Name passes. Junk usually does
                // not.
                if (!ScriptPathShape().IsMatch(script))
                {
                    if (!shapeWarned)
                    {
                        shapeWarned = true;
                        log.Warning(
                            "QuestEventHandler.ScriptPath read {Script}, which is not NNN/Name. The "
                            + "struct layout has most likely moved with a patch; quest scoping is off "
                            + "until it is fixed, and lines will resolve on text alone.",
                            script);
                    }

                    return null;
                }

                return "quest/" + script;
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
