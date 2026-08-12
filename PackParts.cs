namespace GubalLibrary;

/// <summary>
///     One checkbox in the settings window: something a player can name, and the sheets behind it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The name is what the player sees on screen, never the sheet.</b> A sheet name is this
///         project's vocabulary — the same rule the language pack row already follows for the word
///         "page". Nobody who plays the game knows what they are switching off by unticking
///         <c>InstanceContentTextData</c>, and a checkbox whose label only makes sense to whoever
///         wrote it is a checkbox nobody will touch.
///     </para>
///     <para>
///         <b>One part can cover several sheets, and that is the point.</b> <c>HowTo</c>,
///         <c>HowToPage</c> and <c>HowToCategory</c> are one thing to a player — tutorials — and
///         three checkboxes with near-identical names would be worse than none. What is stored is
///         still the sheet keys, because those are facts about the game; what is drawn is the thing.
///     </para>
/// </remarks>
/// <param name="Name">The label. Reads as something seen in the game, not as a file.</param>
/// <param name="Description">
///     <b>Where on screen this is, and what it covers.</b> The whole tooltip for most parts, and the
///     only question its reader has. It says "the paragraph on the right of the Duty Finder", not
///     what happens to a file — how the plugin works is not something a player has to know to
///     decide whether they want their menus in English.
/// </param>
/// <param name="Sheets">The sheet keys it covers, as <see cref="PackParts.SheetOf" /> produces them.</param>
/// <param name="Warning">A reason to think twice, for the few parts that have one. Null for most.</param>
internal sealed record TranslationPart(
    string Name, string Description, string[] Sheets, string? Warning = null);

/// <param name="Name">The heading. Also the label when the group holds a single part.</param>
/// <param name="Description">Where this lot is seen, in one sentence.</param>
/// <param name="Warning">Shown above the description, for a group somebody could regret switching.</param>
/// <param name="Image">
///     Name of a before-and-after picture shipped with the plugin, without its extension, or null.
/// </param>
/// <remarks>
///     A group without a picture is not a defect and draws its tooltip as text. The pictures are
///     screenshots of one build of one language pack, so a group only gets one when somebody has
///     actually taken a pair that shows the difference; there is nothing to generate them from.
/// </remarks>
internal sealed record PartGroup(
    string Name, string Description, TranslationPart[] Parts, string? Warning = null, string? Image = null);

/// <summary>
///     Which parts of a language pack can be switched off, and what each of them is.
/// </summary>
/// <remarks>
///     <para>
///         <b>The plugin owns this table, not the pack.</b> These are the game's own sheets, so the
///         same table describes a Spanish pack, an Italian one and one that has not been built yet —
///         and packs already published keep working without being rebuilt or republished. The pack
///         format is untouched by this feature, deliberately.
///     </para>
///     <para>
///         A sheet this table does not name is <em>not</em> dropped. It is surfaced on its own, under
///         <see cref="OtherGroupName" />, labelled with the only thing that can honestly be said about
///         it — its sheet name. A pack that translates <c>Item</c> or <c>Action</c>, which this corpus
///         does not, is therefore configurable rather than invisible.
///     </para>
/// </remarks>
internal static class PackParts
{
    /// <summary>Where sheets the table does not know end up.</summary>
    public const string OtherGroupName = "Other text in this pack";

    /// <summary>
    ///     The six groups, in the order they are drawn.
    /// </summary>
    /// <remarks>
    ///     Ordered by how much of the game each one covers, so the first thing read is the part that
    ///     matters most and the last is the one nobody will look for.
    /// </remarks>
    public static readonly PartGroup[] Groups =
    [
        new PartGroup(
            "Story and quests",
            "Everything a quest is made of: what people say to you, what you are told to go and do, "
            + "and the subtitles during cutscenes.",
            [
                new TranslationPart(
                    "Quest dialogue and objectives",
                    "The text in the box when you talk to somebody about a quest, and the steps "
                    + "listed under the quest in your Journal and in the tracker down the right of "
                    + "the screen.",
                    ["quest/"]),

                new TranslationPart(
                    "Cutscene subtitles",
                    "The lines across the bottom of the screen while a cutscene is playing.",
                    ["cut_scene/"]),

                // Both journals at once. CompleteJournal is the same quest titles again, listed
                // under completed quests, and there is no reading of "translate the journal" that
                // wants the finished half in a different language from the active half.
                new TranslationPart(
                    "Quest titles in the journal",
                    "Only the names of quests — in the tracker down the right of the screen, in the "
                    + "Journal, and in the list of ones you have finished. Not the text inside them.",
                    ["quest", "completejournal"],
                    "The names and the text are separate boxes, so switching one and not the other "
                    + "gives you English titles over Spanish objectives, or the other way round."),

                new TranslationPart(
                    "\"Ask about...\" menus",
                    "The list of topics an NPC offers when they have several things to tell you, and "
                    + "what they say once you pick one.",
                    ["custom/", "customtalk"]),

                new TranslationPart(
                    "Dungeon, trial and raid dialogue",
                    "What characters and bosses say while you are inside a duty.",
                    ["instancecontenttextdata", "contenttalk"]),

                new TranslationPart(
                    "Open-world content, such as Bozja and Eureka",
                    "The text in the large field zones: Eureka, Bozja, Zadnor and the Occult Crescent.",
                    ["publiccontenttextdata"]),

                new TranslationPart(
                    "Party and large-scale content",
                    "The text in content built for more than one party at a time.",
                    ["partycontenttextdata", "massivepccontenttextdata"]),
            ],
            Image: "story"),

        new PartGroup(
            "NPC chatter",
            "What the people standing around the world say — the talk you can walk past without "
            + "stopping, and the talk you get when you do stop.",
            [
                new TranslationPart(
                    "Small talk when you speak to someone",
                    "The box that opens when you talk to somebody who has nothing to do with a quest.",
                    ["defaulttalk"]),

                // One checkbox for two sheets, decided by looking rather than by reading their
                // names. Balloon is pedlars' cries and idle city chatter; NpcYell is combat barks —
                // and also idle city chatter, and also the red banner that says a zone has been
                // sealed off. Both draw the same little balloon over the same heads, and in testing
                // every balloon that could be found came from NpcYell. Two boxes whose difference
                // nobody can see is a worse answer than one box that covers what people mean.
                new TranslationPart(
                    "Shouts and speech balloons",
                    "The balloons that appear over people's heads as you walk past them, the "
                    + "warnings shouted during a fight, and the red banner across the screen when a "
                    + "zone is sealed off.",
                    ["balloon", "npcyell"]),

                new TranslationPart(
                    "Objects and mechanisms in duties",
                    "The text you get from levers, doors and the other things you can interact with "
                    + "inside a dungeon.",
                    ["gimmicktalk"]),
            ],
            "By far the largest part of the translation. Switching this off is the biggest single "
            + "change you can make here."),

        new PartGroup(
            "Menus and interface",
            "The game's own furniture: window titles, tabs, buttons, and the labels next to your "
            + "numbers.",
            [
                new TranslationPart(
                    "Menus, buttons and window titles",
                    "Everything written on the interface itself — the Character window, the Duty "
                    + "Finder, your inventory, the tabs across the top of a window and the buttons "
                    + "along the bottom.",
                    ["addon"]),

                new TranslationPart(
                    "Title screen and character creation",
                    "The screens before you are in the world: logging in, choosing a character and "
                    + "making one.",
                    ["lobby"]),
            ],
            "Many other Dalamud plugins and combat parsers look for these words in English and stop "
            + "working when they are not. This is the usual reason to switch something off here.\n\n"
            + "Translations also keep proper names in English inside sentences, to match the "
            + "interface you are reading them against. Translating the interface as well leaves "
            + "those names looking inconsistent until the two are brought in line.",
            "interface"),

        new PartGroup(
            "Combat log and system messages",
            "The lines the game writes into your chat log by itself.",
            [
                new TranslationPart(
                    "Combat log and system messages",
                    "What you hit and for how much, gil spent and earned, entering and leaving a "
                    + "sanctuary, duty and party notices, and market board messages.",
                    ["logmessage"]),
            ],
            "Combat parsers and several plugins read these lines in English and will not recognise "
            + "them translated."),

        // The same call as the Duty Finder block: two boxes offering a choice between things nobody
        // wants in different languages.
        new PartGroup(
            "Tutorials and help",
            "The game explaining itself to you.",
            [
                new TranslationPart(
                    "Tutorials and help",
                    "The Active Help windows that appear the first time you do something, the same "
                    + "texts again when you look them up from the main menu afterwards, and the "
                    + "written guides attached to content.",
                    ["howto", "howtopage", "howtocategory", "description", "descriptionstring"]),
            ],
            Image: "help"),

        // One box for all three sheets. Splitting them offered a choice nobody wants to make: there
        // is no reason to read a dungeon's blurb in Spanish and a guildhest's in English, and
        // "Gold Saucer" as its own checkbox invited the reader to wonder what else was hiding.
        // Named after the window it is read in, in the words that window uses. "Duty and content
        // descriptions" was a phrase this project made up; "Duty Finder" is written on screen.
        new PartGroup(
            "Duty Finder descriptions",
            "The blurbs that tell you what something is before you go into it.",
            [
                new TranslationPart(
                    "Duty Finder descriptions",
                    "The paragraph down the right of the Duty Finder when you pick a dungeon, trial "
                    + "or raid; the instructions a guildhest gives you as it starts; and the text "
                    + "around the Gold Saucer and its attractions.",
                    ["contentfinderconditiontransient", "guildorder", "goldsaucertextdata"]),
            ],
            Image: "duty"),
    ];

    /// <summary>Every sheet key the table names, for telling the known from the unknown.</summary>
    private static readonly HashSet<string> Known =
        new(Groups.SelectMany(g => g.Parts).SelectMany(p => p.Sheets), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this sheet belongs to a part above, rather than to the fallback group.</summary>
    public static bool IsKnown(string sheet) => Known.Contains(sheet);

    /// <summary>
    ///     Which part of the translation a page belongs to, from its game path alone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Page names are <c>exd/&lt;sheet&gt;_&lt;firstRowId&gt;[_&lt;language&gt;].exd</c>, so the
    ///         language suffix has to come off before the row id or the row id is never recognised.
    ///     </para>
    ///     <para>
    ///         <b>The trailing slash on the three nested families is not cosmetic.</b> The flat sheet
    ///         <c>Quest</c> — the journal's titles, at <c>exd/quest_65536_en.exd</c> — and the folder
    ///         <c>exd/quest/</c> holding the text of every quest collide by name and are two different
    ///         checkboxes. Returning <c>quest/</c> for one and <c>quest</c> for the other is what keeps
    ///         them apart, here and in what gets saved.
    ///     </para>
    /// </remarks>
    public static string SheetOf(string gamePath)
    {
        var rel = gamePath.StartsWith("exd/", StringComparison.OrdinalIgnoreCase) ? gamePath[4..] : gamePath;

        var slash = rel.IndexOf('/');
        if (slash >= 0)
        {
            return rel[..(slash + 1)].ToLowerInvariant();
        }

        var name = Path.GetFileNameWithoutExtension(rel);
        var tokens = name.Split('_');
        var end = tokens.Length;

        // A short all-letter tail is the language slot the pack overwrote — "en" for this corpus,
        // but a pack may target any of them, so it is recognised by shape rather than by value.
        if (end > 1 && tokens[end - 1].Length <= 4 && tokens[end - 1].All(char.IsAsciiLetter))
        {
            end--;
        }

        // Then the page's first row id. Sheets that are not localised have no language tail and go
        // straight to this one.
        if (end > 1 && tokens[end - 1].All(char.IsAsciiDigit))
        {
            end--;
        }

        return string.Join('_', tokens[..end]).ToLowerInvariant();
    }
}
