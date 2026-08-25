using CheapLoc;

namespace GubalLibrary;

/// <summary>
///     One checkbox in the settings window: something a player can name, and the sheets behind it.
/// </summary>
/// <remarks>
///     <b>The name is what the player sees on screen, never the sheet.</b> Nobody who plays the game
///     knows what they are switching off by unticking <c>InstanceContentTextData</c>. <b>One part can
///     cover several sheets</b>, and that is the point: <c>HowTo</c>, <c>HowToPage</c> and
///     <c>HowToCategory</c> are one thing to a player. What is stored is still the sheet keys,
///     because those are facts about the game.
/// </remarks>
/// <param name="Name">The label. Reads as something seen in the game, not as a file.</param>
/// <param name="Description">Where on screen this is and what it covers — the whole tooltip for most parts.</param>
/// <param name="Sheets">The sheet keys it covers, as <see cref="PackParts.SheetOf" /> produces them.</param>
/// <param name="Warning">A reason to think twice, for the few parts that have one. Null for most.</param>
/// <param name="Image">
///     A before-and-after picture of this part alone. A part inside a group otherwise lets the
///     group's stand for all of it, which is wrong the moment one of them has a pair that is about
///     it and not about its neighbours.
/// </param>
internal sealed record TranslationPart(
    string Name, string Description, string[] Sheets, string? Warning = null, string? Image = null);

/// <param name="Name">The heading. Also the label when the group holds a single part.</param>
/// <param name="Description">Where this lot is seen, in one sentence.</param>
/// <param name="Warning">Shown above the description, for a group somebody could regret switching.</param>
/// <param name="Image">
///     Name of a before-and-after picture shipped with the plugin, without its extension, or null. A
///     group without one draws its tooltip as text: the pictures are screenshots of one build of one
///     pack, so a group only gets one when somebody has actually taken the pair.
/// </param>
internal sealed record PartGroup(
    string Name, string Description, TranslationPart[] Parts, string? Warning = null, string? Image = null);

/// <summary>
///     Which parts of a language pack can be switched off, and what each of them is.
/// </summary>
/// <remarks>
///     <b>The plugin owns this table, not the pack.</b> These are the game's own sheets, so the same
///     table describes a Spanish pack, an Italian one and one not built yet, and published packs keep
///     working without being rebuilt. A sheet the table does not name is not dropped: it is surfaced
///     under <see cref="OtherGroupName" />, labelled with the only thing that can honestly be said
///     about it — its sheet name.
/// </remarks>
internal static class PackParts
{
    /// <summary>Where sheets the table does not know end up.</summary>
    public static string OtherGroupName =>
        Loc.Localize("Group.Other.Name", "Other text in this pack");

    // A CARVE-OUT LIVED HERE AND WAS REMOVED ON 14 AUGUST 2026. Read this before adding another. The
    // five retainer conversations were given a key of their own, `custom/retainer`, so a player could
    // have those windows in English without giving up every NPC menu. It could not: the retainer
    // WINDOW is drawn from `Addon` (#2377-#2407); the conversations hold what the retainer SAYS. So
    // the checkbox promised a window and delivered the chatter, which is worse than no checkbox. A
    // split is only worth it when it matches something the player can point at.

    /// <summary>
    ///     The groups, in the order they are drawn.
    /// </summary>
    /// <remarks>
    ///     Ordered the way somebody reads down them looking for a thing: the story, the people in it,
    ///     what happens inside content, the windows around all of it, then the two nobody comes here
    ///     for. Close to descending size and not the same rule — the Duty Finder is 751 rows and sits
    ///     fourth, because it is read beside the content it describes. <b>Every one of the pack's 28
    ///     keys is named exactly once below</b>, each measured rather than guessed from its name; the
    ///     evidence is in <c>issues/pack-page-inventory.md</c>.
    /// </remarks>
    public static PartGroup[] Groups => groups ??= Build();

    /// <summary>Built on demand and kept, because every string in it goes through CheapLoc.</summary>
    /// <remarks>
    ///     Dropped by <see cref="Invalidate" /> when the language changes. A <c>static readonly</c>
    ///     array would have been built once, in whatever language was loaded at the time, and would
    ///     still be in it after somebody switched Dalamud to another one.
    /// </remarks>
    private static PartGroup[]? groups;

    /// <summary>Forgets the table so the next reader rebuilds it in the current language.</summary>
    public static void Invalidate() => groups = null;

    private static PartGroup[] Build() =>
    [
        new PartGroup(
            Loc.Localize("Group.Story.Name", "Quests and cutscenes"),
            Loc.Localize("Group.Story.Desc",
                "Everything a quest is made of: what people say to you, what you are told to go and "
                + "do, and the subtitles while a cutscene plays."),
            [
                // One box for all four kinds of row in quest/: they share a page per quest and cannot
                // be offered apart.
                new TranslationPart(
                    Loc.Localize("Part.QuestText.Name", "Quest dialogue, journal and objectives"),
                    Loc.Localize("Part.QuestText.Desc",
                        "The text in the box when you talk to somebody about a quest, the summary "
                        + "written into your Journal as the story advances, the steps listed in the "
                        + "tracker down the right of the screen, and the notices a quest posts while "
                        + "you are on it."),
                    ["quest/"]),

                new TranslationPart(
                    Loc.Localize("Part.Cutscenes.Name", "Cutscene subtitles"),
                    Loc.Localize("Part.Cutscenes.Desc",
                        "The lines across the bottom of the screen while a cutscene is playing."),
                    ["cut_scene/"]),

                // BOTH TITLE SHEETS: 5,367 of CompleteJournal's 7,598 rows are byte-identical to a
                // row in Quest, and the Journal shows both at once — which is how the mismatch was
                // found, «Acechantes en la gruta» on the right and "Lurkers in the Grotto" on the left.
                new TranslationPart(
                    Loc.Localize("Part.QuestNames.Name", "Quest names"),
                    Loc.Localize("Part.QuestNames.Desc",
                        "Only the names of quests, levequests and duties: in the tracker, in the "
                        + "Journal, and in the Unending Journey at an inn where you replay "
                        + "cutscenes. Not the text inside them."),
                    ["quest", "completejournal"],
                    Loc.Localize("Part.QuestNames.Warning",
                        "The names and the text are separate boxes, so switching one and not the "
                        + "other gives you English titles over translated objectives, or the other "
                        + "way round.")),
            ],
            Image: "story"),

        new PartGroup(
            Loc.Localize("Group.People.Name", "What the people around you say"),
            Loc.Localize("Group.People.Desc",
                "The talk you can walk past without stopping, and the talk you get when you do stop."),
            [
                // GoldSaucerTalk is here and not in the interface box, where its near-namesake
                // GoldSaucerTextData went: that one is scoreboards and race courses, this one is the
                // Mini Cactpot crier working the crowd. The purchase prompt rides along because it is
                // the same conversation.
                new TranslationPart(
                    Loc.Localize("Part.Talk.Name", "Talking to someone"),
                    Loc.Localize("Part.Talk.Desc",
                        "The box that opens when you speak to somebody who has nothing to do with a "
                        + "quest, including the Gold Saucer's criers and the attendants who sell you "
                        + "a Cactpot ticket."),
                    ["defaulttalk", "goldsaucertalk"]),

                // HONESTLY TWO THINGS, AND IT SAYS SO. custom/ is 83% people talking, but the service
                // windows are in there too and cannot be separated: CmnDefRetainerBell_00544 is 785
                // rows of menu entries and retainer small talk in ONE file. CustomTalk is the list of
                // verbs picked from before any of it is spoken, and has to travel with custom/ or the
                // menu and the answer end up in different languages.
                new TranslationPart(
                    Loc.Localize("Part.AskAbout.Name", "\"Ask about...\" menus and service windows"),
                    Loc.Localize("Part.AskAbout.Desc",
                        "The list of topics an NPC offers when they have several things to tell you, "
                        + "what they say once you pick one, and the windows you work in afterwards: "
                        + "the retainer, the aetheryte, the levequest board, linkshells, relic "
                        + "trade-ins and your estate."),
                    ["custom/", "customtalk"]),

                // Two sheets, one checkbox, decided by looking. Balloon leans towards pedlars' cries
                // and NpcYell towards combat barks, but only by a lean: every lexical probe separates
                // them by under 3%, 265 strings appear in both word for word, and in testing every
                // balloon found came from NpcYell.
                //
                // THE RED "SEALED OFF" BANNER IS NOT HERE. It is LogMessage#2012-#2013, so it belongs
                // to the chat log box; zero matches in either of these.
                new TranslationPart(
                    Loc.Localize("Part.Balloons.Name", "Shouts and speech balloons"),
                    Loc.Localize("Part.Balloons.Desc",
                        "The balloons that appear over people's heads as you walk past them, and the "
                        + "warnings shouted during a fight."),
                    ["balloon", "npcyell"]),
            ],
            Loc.Localize("Group.People.Warning",
                "By far the largest part of the translation. Switching this off is the biggest "
                + "single change you can make here.")),

        // FIVE SHEETS IN ONE BOX, WHICH IS A MERGE AND NOT A SHRUG: they are the same kind of sheet,
        // everything one type of content produces. And NONE OF IT IS AMBIENT, which is where they
        // nearly went — «Clear the binding lock» is an objective and «Successful catches» a
        // scoreboard, so filing it as flavour would quietly take objectives off the screen.
        new PartGroup(
            Loc.Localize("Group.Duties.Name", "Duties, raids and field operations"),
            Loc.Localize("Group.Duties.Desc",
                "What happens once you are inside a dungeon, a raid, or one of the large field zones."),
            [
                // VVDVoteRouteLabel travels with ContentTalk because THEY HOLD THE SAME SENTENCE, and
                // the variant dungeon's voting window reads the VVD one — which is how it came to be
                // English while the Spanish sat in ContentTalk, translated and never used.
                new TranslationPart(
                    Loc.Localize("Part.DutyText.Name", "Dialogue, objectives and on-screen text"),
                    Loc.Localize("Part.DutyText.Desc",
                        "What bosses and NPCs say while you are inside, the objectives that appear as "
                        + "it goes on, the route your party votes on in a variant dungeon, and the "
                        + "same for the large field zones and the big group content: Eureka, Bozja, "
                        + "Zadnor, the Occult Crescent, the Ishgardian Restoration and the Diadem."),
                    ["instancecontenttextdata", "contenttalk", "publiccontenttextdata",
                     "massivepccontenttextdata", "partycontenttextdata", "vvdvoteroutelabel"]),

                // WHAT THE OBJECT SAYS, NOT WHAT IT IS CALLED — the name under the cursor is EObjName,
                // in the interface group. The two differ in voice: GimmickTalk narrates at you,
                // GimmickBill is the document itself with no narrator.
                new TranslationPart(
                    Loc.Localize("Part.Objects.Name", "What an object tells you when you use it"),
                    Loc.Localize("Part.Objects.Desc",
                        "The message that comes back when you examine a corpse, pull a lever or open "
                        + "a panel, and the documents you find and read in full, such as expedition "
                        + "journals, letters, and the scrawled memos that give away a puzzle's "
                        + "answer."),
                    ["gimmicktalk", "gimmickbill"],
                    Image: "examine"),

                // Named after the kind of thing rather than after any one duty: the first draft was
                // "the Occult Crescent's own windows" and would have needed renaming the moment the
                // deep dungeon sheets were extracted a day later.
                new TranslationPart(
                    Loc.Localize("Part.DutyItems.Name", "The items, jobs and gear found only inside them"),
                    Loc.Localize("Part.DutyItems.Desc",
                        "The things that exist in one piece of content and nowhere else: the "
                        + "pomanders and aetherpool weapons of the deep dungeons, the floor effects "
                        + "announced as you descend, and the Occult Crescent's phantom jobs, their "
                        + "traits, and the lore log it fills in as you explore."),
                    ["mkdsupportjob", "mkdtrait", "mkdlore", "deepdungeonitem",
                     "deepdungeonequipment", "deepdungeonflooreffectui", "deepdungeondemiclone",
                     "eurekaaetheritem"]),
            ]),

        // One box for the whole window: the roulettes and the duties below them are one list to the
        // person reading it. GuildOrder rides along because a guildhest's briefing is drawn in the
        // same panel. Its own group rather than part of the interface, because it is read beside the
        // content it describes.
        new PartGroup(
            Loc.Localize("Group.DutyFinder.Name", "Duty Finder"),
            Loc.Localize("Group.DutyFinder.Desc",
                "The blurbs that tell you what something is before you go into it."),
            [
                new TranslationPart(
                    Loc.Localize("Part.DutyFinder.Name", "Duty Finder"),
                    Loc.Localize("Part.DutyFinder.Desc",
                        "The Duty Finder from top to bottom: the roulettes and what each one asks of "
                        + "you, including chocobo racing and ranked PvP, the paragraph down the "
                        + "right when you pick a dungeon, trial or raid, and the briefing a guildhest "
                        + "gives you as it starts."),
                    ["contentfinderconditiontransient", "contentroulette", "guildorder"]),
            ],
            Image: "duty"),

        new PartGroup(
            Loc.Localize("Group.Interface.Name", "Menus and interface"),
            Loc.Localize("Group.Interface.Desc",
                "The game's own furniture: window titles, tabs, buttons, and the labels next to your "
                + "numbers."),
            [
                // THREE SHEETS, AND THE TWO PASSENGERS ARE HERE ON PURPOSE. RetainerTaskRandom is the
                // venture names, read in a window that IS `addon`. GoldSaucerTextData is an `addon`
                // for one content area — courses, grades, HUD counters. MainCommand is the main menu,
                // which had been falling through to the leftovers box under its own sheet name.
                new TranslationPart(
                    Loc.Localize("Part.Menus.Name", "Menus, buttons and window titles"),
                    Loc.Localize("Part.Menus.Desc",
                        "Everything written on the interface itself: the main menu you open with "
                        + "Esc, the Character window with your attributes and what each one does, "
                        + "the Duty Finder, your inventory, the retainer windows and their venture "
                        + "list, the Gold Saucer's scoreboards and race courses, the tabs across the "
                        + "top of a window and the buttons along the bottom."),
                    //`baseparam` is the Character window's attribute names and the sentence each one
                    //shows on hover. Its own box would be a checkbox for half of one panel.
                    ["addon", "maincommand", "retainertaskrandom", "goldsaucertextdata", "baseparam"],
                    // The pair is the Character window, which is `addon` and nothing else in this
                    // group; hung off the group it promised its neighbours somebody else's screenshot.
                    Image: "interface"),

                // TWO SHEETS THAT MUST SHARE A BOX, for the VVDVoteRouteLabel reason: THEY HOLD THE
                // SAME SENTENCE. «Radiant's Gear Augmentation (IL 600)» is SpecialShop#1770447 and
                // also TopicSelect#3276940, and the vendor's menu reads the TopicSelect one — which
                // is why translating SpecialShop alone left the window in English.
                new TranslationPart(
                    Loc.Localize("Part.Shops.Name", "Shop and exchange windows"),
                    Loc.Localize("Part.Shops.Desc",
                        "The title on a vendor's window and the list of shops they offer before you "
                        + "pick one: the tomestone exchanges, the gear sets listed by item level, and "
                        + "the seasonal event stalls."),
                    ["specialshop", "topicselect"]),

                // THE NAME UNDER THE CURSOR IS INTERFACE, not scenery: a label the game draws over the
                // world, and this group's warning — other plugins read these words in English —
                // applies to it more than to anything else here. 16,146 rows over 4,626 distinct
                // strings, «destination» alone 3,279 of them.
                new TranslationPart(
                    Loc.Localize("Part.WorldObjects.Name", "Names of things you can interact with"),
                    Loc.Localize("Part.WorldObjects.Desc",
                        "What the cursor reads when you point at something in the world: aetherytes "
                        + "and the destinations they offer, Aethernet shards, aether currents, "
                        + "levers, doors, gathering nodes, the signs and notes you stop to read, and "
                        + "the treasure coffers a duty leaves behind."),
                    // THREE SHEETS, ONE BOX, BECAUSE THE PLAYER POINTS AT ONE THING. `eobjname` is
                    // the name of anything with a cursor target; `aetheryte` holds the two the game
                    // keeps apart — «aetheryte» and «Aethernet shard» — and `treasure` the lettered
                    // chests a duty leaves, «treasure coffer A» and its B and C. All three were
                    // found the same way: a sheet was translated, the thing still read English on
                    // screen, and the word turned out to live somewhere else. Split into separate
                    // checkboxes they would let somebody translate the coffer and not its letter.
                    ["eobjname", "aetheryte", "treasure"],
                    Image: "interactable"),

                new TranslationPart(
                    Loc.Localize("Part.Lobby.Name", "Title screen and character creation"),
                    Loc.Localize("Part.Lobby.Desc",
                        "The screens before you are in the world: logging in, choosing a character, "
                        + "and the races, clans and options you pick from when making one."),
                    ["lobby"]),
            ],
            Loc.Localize("Group.Interface.Warning",
                "Many other Dalamud plugins and combat parsers look for these words in English and "
                + "stop working when they are not. This is the usual reason to switch something off "
                + "here.\n\nTranslations also keep proper names in English inside sentences, to match "
                + "the interface you are reading them against. Translating the interface as well "
                + "leaves those names looking inconsistent until the two are brought in line.")),

        // TWO BOXES, AND THEY USED TO BE ONE. Active Help interrupts you; a content guide is a window
        // you go and open. DescriptionString alone is 1,576 rows with entries up to 3,258 characters
        // — the mahjong rulebook, the Bozja briefing — and nobody reads that as a tutorial popup.
        new PartGroup(
            Loc.Localize("Group.Guides.Name", "Tutorials and guides"),
            Loc.Localize("Group.Guides.Desc", "The game explaining itself to you."),
            [
                // MultipleHelpString was given its own box on 16 August and merged the same day: the
                // trigger differs, what the text IS does not, and 37 rows against 969 is not a split
                // anybody would go looking for.
                new TranslationPart(
                    Loc.Localize("Part.ActiveHelp.Name", "Active Help and window help"),
                    Loc.Localize("Part.ActiveHelp.Desc",
                        "The windows that pop up the first time you do something, the same texts "
                        + "again when you look them up from the main menu afterwards, and the help "
                        + "behind the question mark in the corner of a window, such as the Duty "
                        + "Finder's pages on registering for a duty and what happens next."),
                    ["howto", "howtopage", "howtocategory", "multiplehelpstring", "multiplehelp"],
                    Image: "help"),

                new TranslationPart(
                    Loc.Localize("Part.ContentGuides.Name", "Content guides"),
                    Loc.Localize("Part.ContentGuides.Desc",
                        "The written guide a content window opens: the rules of mahjong and Triple "
                        + "Triad, and the briefings for Bozja, deep dungeons, the Island Sanctuary "
                        + "and New Game+."),
                    // `descriptionstandalonetransient` holds the TITLE above these pages. Leave it
                    // out and the title reads English over a Spanish page, which is how it was found.
                    ["description", "descriptionstring", "descriptionstandalonetransient"],
                    Image: "contentguide"),
            ]),

        // One box for 8,469 rows a player would happily split and cannot. LogKind isolates the battle
        // log cleanly, but the duty announcements sit in a 4,495-row bucket with party invites and
        // system notices, so there is no line to cut on. Parked in WORKQUEUE.
        new PartGroup(
            Loc.Localize("Group.Log.Name", "Combat log and system messages"),
            Loc.Localize("Group.Log.Desc", "The lines the game writes into your chat log by itself."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Log.Name", "Combat log and system messages"),
                    Loc.Localize("Part.Log.Desc",
                        "Everything the game writes into your chat log by itself: what you hit and "
                        + "for how much, emotes, gil spent and earned, party and Free Company "
                        + "notices, gathering and crafting, market board messages, the announcements "
                        + "a duty makes including the red banner when a zone is sealed off, and every "
                        + "\"unable to\" the game answers with."),
                    ["logmessage"]),
            ],
            Loc.Localize("Group.Log.Warning",
                "Combat parsers and several plugins read these lines in English and will not "
                + "recognise them translated.")),
    ];

    /// <summary>Every sheet key the table names, for telling the known from the unknown.</summary>
    /// <remarks>Built once and never invalidated: sheet keys are the game's, and no language moves them.</remarks>
    private static readonly HashSet<string> Known =
        new(Groups.SelectMany(g => g.Parts).SelectMany(p => p.Sheets), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this sheet belongs to a part above, rather than to the fallback group.</summary>
    public static bool IsKnown(string sheet) => Known.Contains(sheet);

    /// <summary>
    ///     Which part of the translation a page belongs to, from its game path alone.
    /// </summary>
    /// <remarks>
    ///     Page names are <c>exd/&lt;sheet&gt;_&lt;firstRowId&gt;[_&lt;language&gt;].exd</c>, so the
    ///     language suffix comes off before the row id or the row id is never recognised.
    ///     <b>The trailing slash on the nested families is not cosmetic</b>: the flat sheet
    ///     <c>Quest</c> and the folder <c>exd/quest/</c> are two different checkboxes, and returning
    ///     <c>quest/</c> for one and <c>quest</c> for the other is what keeps them apart.
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

        // A short all-letter tail is the language slot the pack overwrote. Recognised by shape rather
        // than by value, because a pack may target any of them.
        if (end > 1 && tokens[end - 1].Length <= 4 && tokens[end - 1].All(char.IsAsciiLetter))
        {
            end--;
        }

        // Then the page's first row id. Sheets that are not localised have no language tail and come
        // straight here.
        if (end > 1 && tokens[end - 1].All(char.IsAsciiDigit))
        {
            end--;
        }

        return string.Join('_', tokens[..end]).ToLowerInvariant();
    }
}
