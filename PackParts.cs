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
/// <param name="Image">A before-and-after picture of this part alone, when the group's would mislead.</param>
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
///     <b>The plugin owns this table, not the pack</b>, so it describes a pack in any language and one
///     not built yet. A sheet it does not name still shows, under <see cref="OtherGroupName" />.
/// </remarks>
internal static class PackParts
{
    /// <summary>Where sheets the table does not know end up.</summary>
    public static string OtherGroupName =>
        Loc.Localize("Group.Other.Name", "Other text in this pack");

    // Split a group only where the split matches something the player can point at.

    /// <summary>
    ///     The groups, in the order they are drawn.
    /// </summary>
    /// <remarks>
    ///     Ordered the way somebody reads down them looking for a thing, not by size. <b>Every one of
    ///     the pack's 31 keys is named exactly once below</b>, and matching is exact: a key not listed
    ///     falls to the leftovers box, so a sheet is never covered by a similarly named neighbour.
    /// </remarks>
    public static PartGroup[] Groups => groups ??= Build();

    /// <summary>Built on demand and kept, because every string in it goes through CheapLoc.</summary>
    /// <remarks>Dropped by <see cref="Invalidate" /> on a language change; a static array would keep
    /// whichever language was loaded when it was built.</remarks>
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
                // One box for all four kinds of row in quest/: they share a page and cannot be split.
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

                // The scene before the player has a quest at all, so it belongs with the story
                // rather than with the city NPCs who say the same kind of thing afterwards.
                new TranslationPart(
                    Loc.Localize("Part.Opening.Name", "The opening scene of your starting city"),
                    Loc.Localize("Part.Opening.Desc",
                        "What the first NPC says to a brand new character, before the first quest."),
                    ["opening/"]),

                // Both title sheets: 5,367 CompleteJournal rows duplicate Quest and the Journal shows both.
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
                // GoldSaucerTalk is the Cactpot crier; GoldSaucerTextData is scoreboards, in the interface box.
                new TranslationPart(
                    Loc.Localize("Part.Talk.Name", "Talking to someone"),
                    Loc.Localize("Part.Talk.Desc",
                        "The box that opens when you speak to somebody who has nothing to do with a "
                        + "quest, including the Gold Saucer's criers and the attendants who sell you "
                        + "a Cactpot ticket."),
                    ["defaulttalk", "goldsaucertalk"]),

                // custom/ mixes talk and service windows in one file, and CustomTalk must travel with it.
                new TranslationPart(
                    Loc.Localize("Part.AskAbout.Name", "\"Ask about...\" menus and service windows"),
                    Loc.Localize("Part.AskAbout.Desc",
                        "The list of topics an NPC offers when they have several things to tell you, "
                        + "what they say once you pick one, and the windows you work in afterwards: "
                        + "the retainer, the aetheryte, the levequest board, linkshells, relic "
                        + "trade-ins and your estate."),
                    ["custom/", "customtalk"]),

                // The inn menu is split across custom/ and warp/, and transport/ is the same window
                // one destination further out. Three families, one thing to a player.
                new TranslationPart(
                    Loc.Localize("Part.InnsAndTravel.Name", "Inns, aetherytes and chocobo porters"),
                    Loc.Localize("Part.InnsAndTravel.Desc",
                        "The attendant who greets you at an inn and the menu they open, the aethernet "
                        + "list you pick a shard from, the chocobo porter stands, the rental stables "
                        + "and the wedding desk."),
                    ["warp/", "transport/"]),

                // The levemete's own window, not the leve text: that is in the quest group.
                new TranslationPart(
                    Loc.Localize("Part.Counters.Name", "Levemete and exchange counters"),
                    Loc.Localize("Part.Counters.Desc",
                        "The window a levemete opens when you hand work in, and the titles and "
                        + "buttons of the counters where you exchange tokens for something else."),
                    ["leve/", "shop/"]),

                // Balloon and NpcYell separate by under 3% on every probe and share 265 strings word for word.
                //
                // The red "sealed off" banner is LogMessage#2012-#2013, so it belongs to the chat log box.
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

        // Five sheets, one box: everything a type of content produces, and none of it is ambient flavour.
        new PartGroup(
            Loc.Localize("Group.Duties.Name", "Duties, raids and field operations"),
            Loc.Localize("Group.Duties.Desc",
                "What happens once you are inside a dungeon, a raid, or one of the large field zones."),
            [
                // VVDVoteRouteLabel and ContentTalk hold the same sentence; the voting window reads the VVD one.
                new TranslationPart(
                    Loc.Localize("Part.DutyText.Name", "Dialogue, objectives and on-screen text"),
                    Loc.Localize("Part.DutyText.Desc",
                        "What bosses and NPCs say while you are inside, the objectives that appear as "
                        + "it goes on, the route your party votes on in a variant dungeon, and the "
                        + "same for the large field zones and the big group content: Eureka, Bozja, "
                        + "Zadnor, the Occult Crescent, the Ishgardian Restoration and the Diadem."),
                    ["instancecontenttextdata", "contenttalk", "publiccontenttextdata",
                     "massivepccontenttextdata", "partycontenttextdata", "vvdvoteroutelabel",
                     // dungeon/ is boss voices, not menus: «We are Calcabrina! Adorable dolls!»
                     "dungeon/"]),

                // The NPC standing outside, not the one inside: raid/ meets you at the entrance and
                // content/ is the deep dungeons' own cast.
                new TranslationPart(
                    Loc.Localize("Part.DutyGuides.Name", "The guides who wait outside them"),
                    Loc.Localize("Part.DutyGuides.Desc",
                        "The NPC at a raid entrance who explains what lies beyond and the menu they "
                        + "open, the cast that stands at the bottom of the deep dungeons, and the "
                        + "guildhest guide's window."),
                    ["raid/", "content/", "guild_order/"]),

                // What the object says; what it is called is EObjName, in the interface group.
                new TranslationPart(
                    Loc.Localize("Part.Objects.Name", "What an object tells you when you use it"),
                    Loc.Localize("Part.Objects.Desc",
                        "The message that comes back when you examine a corpse, pull a lever or open "
                        + "a panel, and the documents you find and read in full, such as expedition "
                        + "journals, letters, and the scrawled memos that give away a puzzle's "
                        + "answer, plus the yes-or-no it asks before acting."),
                    // `gimmickyesno` carries its own Yes and No buttons, so without it the prompt
                    // reads Spanish over two English buttons.
                    ["gimmicktalk", "gimmickbill", "gimmickyesno"],
                    Image: "examine"),

                // Named after the kind of thing, never one duty, so a new content sheet does not force a rename.
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

        // One window to the reader: roulettes, the duties below them, and a guildhest's briefing.
        new PartGroup(
            Loc.Localize("Group.DutyFinder.Name", "Duty Finder"),
            Loc.Localize("Group.DutyFinder.Desc",
                "The blurbs that tell you what something is before you go into it."),
            [
                new TranslationPart(
                    Loc.Localize("Part.DutyFinder.Name", "Duty Finder"),
                    Loc.Localize("Part.DutyFinder.Desc",
                        "The Duty Finder from top to bottom: the name of every dungeon, trial and "
                        + "raid in the list, the roulettes and what each one asks of you, including "
                        + "chocobo racing and ranked PvP, the paragraph down the right when you pick "
                        + "one, and the briefing a guildhest gives you as it starts."),
                    ["contentfinderconditiontransient", "contentfindercondition", "contentroulette", "guildorder"]),
            ],
            Image: "duty"),

        new PartGroup(
            Loc.Localize("Group.Interface.Name", "Menus and interface"),
            Loc.Localize("Group.Interface.Desc",
                "The game's own furniture: window titles, tabs, buttons, and the labels next to your "
                + "numbers."),
            [
                // RetainerTaskRandom and GoldSaucerTextData are `addon` windows for one area.
                // MainCommandCategory is the same menu as MainCommand: its seven headings file those
                // entries, and one without the other draws Spanish rows under English headings.
                new TranslationPart(
                    Loc.Localize("Part.Menus.Name", "Menus, buttons and window titles"),
                    Loc.Localize("Part.Menus.Desc",
                        "Everything written on the interface itself: the main menu you open with "
                        + "Esc, the Character window with your attributes and what each one does, "
                        + "the Duty Finder, your inventory, the retainer windows and their venture "
                        + "list, the Gold Saucer's scoreboards and race courses, the tabs across the "
                        + "top of a window and the buttons along the bottom."),
                    // `baseparam` is the Character window's attributes and their hover text, and
                    // `itemspecialbonus` the heading a tooltip puts over a conditional bonus.
                    ["addon", "maincommand", "maincommandcategory", "retainertaskrandom", "goldsaucertextdata", "baseparam", "itemspecialbonus"],
                    // The pair is the Character window, which is `addon` alone in this group.
                    Image: "interface"),

                // SpecialShop and TopicSelect hold the same sentence, and the vendor's menu reads TopicSelect.
                new TranslationPart(
                    Loc.Localize("Part.Shops.Name", "Shop and exchange windows"),
                    Loc.Localize("Part.Shops.Desc",
                        "The title on a vendor's window and the list of shops they offer before you "
                        + "pick one: the tomestone exchanges, the gear sets listed by item level, and "
                        + "the seasonal event stalls, with the two dropdowns an exchange window sorts "
                        + "its wares by."),
                    ["specialshop", "topicselect", "inclusionshopcategory"]),

                // The name under the cursor is interface, not scenery: a label the game draws over the world.
                new TranslationPart(
                    Loc.Localize("Part.WorldObjects.Name", "Names of things you can interact with"),
                    Loc.Localize("Part.WorldObjects.Desc",
                        "What the cursor reads when you point at something in the world: aetherytes "
                        + "and the destinations they offer, Aethernet shards, aether currents, "
                        + "levers, doors, gathering nodes, the signs and notes you stop to read, and "
                        + "the treasure coffers a duty leaves behind."),
                    // Four sheets, one thing the player points at: the object's name, the two
                    // aetheryte kinds the game keeps apart, the lettered coffers a duty leaves, and
                    // the travel menu an aetheryte or a ferryman opens with its confirmation.
                    ["eobjname", "aetheryte", "treasure", "warp"],
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

        // Two boxes: Active Help interrupts you, a content guide is a window you go and open.
        new PartGroup(
            Loc.Localize("Group.Guides.Name", "Tutorials and guides"),
            Loc.Localize("Group.Guides.Desc", "The game explaining itself to you."),
            [
                // MultipleHelpString rides along: the trigger differs, the text does not, 37 rows against 969.
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
                    // `descriptionstandalonetransient` is the TITLE above these pages; without it the
                    // title reads English over a translated page.
                    ["description", "descriptionstring", "descriptionstandalonetransient"],
                    Image: "contentguide"),
            ]),

        // One box: LogKind isolates the battle log, but duty announcements share a bucket with party
        // invites and system notices, so there is no line to cut on.
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

                // A prompt, not a log line, but the same kind of thing to a player: the world
                // stopping you and asking. `story/` is one row and the game itself says to delete it.
                new TranslationPart(
                    Loc.Localize("Part.WorldPrompts.Name", "What the world asks before it lets you pass"),
                    Loc.Localize("Part.WorldPrompts.Desc",
                        "The prompt that stops you at a boundary and its buttons, such as being asked "
                        + "to dismount before going further."),
                    ["system/", "story/"]),
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

        // A short all-letter tail is the language slot, read by shape because a pack may target any.
        if (end > 1 && tokens[end - 1].Length <= 4 && tokens[end - 1].All(char.IsAsciiLetter))
        {
            end--;
        }

        // Then the page's first row id. Unlocalised sheets have no language tail and come straight here.
        if (end > 1 && tokens[end - 1].All(char.IsAsciiDigit))
        {
            end--;
        }

        return string.Join('_', tokens[..end]).ToLowerInvariant();
    }
}
