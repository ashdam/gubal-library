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
/// <param name="Image">
///     A before-and-after picture of this part alone, for the few that have one of their own. A part
///     inside a group otherwise shows no picture and lets the group's stand for all of it, which is
///     right while the parts of a group are variations on one thing and wrong the moment one of them
///     has a pair of screenshots that is about it and not about its neighbours.
/// </param>
internal sealed record TranslationPart(
    string Name, string Description, string[] Sheets, string? Warning = null, string? Image = null);

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

    // A CARVE-OUT LIVED HERE AND WAS REMOVED ON 14 AUGUST 2026. Read this before adding another.
    //
    // The five retainer conversations were given a key of their own, `custom/retainer`, so that a
    // player running a retainer plugin could have those windows back in English without giving up
    // every NPC menu in the game. It did not do that, and could not: the retainer WINDOW is drawn
    // from `Addon` — `Addon#2377` is «Retainer: … Ventures: … Select an option.», all three lines in
    // one row, with `#2378`-`#2407` beneath it. The conversations hold what the retainer SAYS.
    //
    // So the checkbox promised a window and delivered the chatter, which is worse than no checkbox:
    // somebody unticks it, restarts, sees the menu still in Spanish and concludes the plugin is
    // broken. Splitting a folder is only worth it when the split matches something the player can
    // point at, and this one matched the file layout instead.

    /// <summary>
    ///     The groups, in the order they are drawn.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ordered the way somebody reads down them looking for a thing: the story, then the
    ///         people in it, then what happens inside content, then the windows around all of it, and
    ///         last the two nobody comes here for. That is close to descending size and is not the
    ///         same rule — the Duty Finder is 751 rows and sits fourth, because it is read beside the
    ///         content it describes and not beside the character creator.
    ///     </para>
    ///     <para>
    ///         <b>Every one of the pack's 28 keys is named exactly once below.</b> The table was
    ///         rewritten on 14 August 2026 after each key was measured — row counts, median lengths,
    ///         and what the text actually is — because two of the descriptions had been written from a
    ///         sheet's name and were wrong on screen. The evidence is in
    ///         <c>issues/pack-page-inventory.md</c>.
    ///     </para>
    /// </remarks>
    public static readonly PartGroup[] Groups =
    [
        new PartGroup(
            "Quests and cutscenes",
            "Everything a quest is made of: what people say to you, what you are told to go and do, "
            + "and the subtitles while a cutscene plays.",
            [
                // One box for all four kinds of row in quest/, because they share a page per quest
                // and cannot be offered apart: 210,430 spoken lines, 24,000 journal summaries, 20,067
                // tracker steps and 8,341 system notices.
                new TranslationPart(
                    "Quest dialogue, journal and objectives",
                    "The text in the box when you talk to somebody about a quest, the summary written "
                    + "into your Journal as the story advances, the steps listed in the tracker down "
                    + "the right of the screen, and the notices a quest posts while you are on it.",
                    ["quest/"]),

                new TranslationPart(
                    "Cutscene subtitles",
                    "The lines across the bottom of the screen while a cutscene is playing.",
                    ["cut_scene/"]),

                // BOTH TITLE SHEETS, AND THE REASON IS MEASURED. 5,367 of CompleteJournal's 7,598
                // rows are byte-identical to a row in Quest: the same title, once for a quest you are
                // on and once for one you have finished. And the Journal shows both at once — which
                // is how the mismatch was found, with «Acechantes en la gruta» on the right of that
                // window and "Lurkers in the Grotto" in the list on the left.
                new TranslationPart(
                    "Quest names",
                    "Only the names of quests, levequests and duties — in the tracker, in the "
                    + "Journal, and in the Unending Journey at an inn where you replay cutscenes. Not "
                    + "the text inside them.",
                    ["quest", "completejournal"],
                    "The names and the text are separate boxes, so switching one and not the other "
                    + "gives you English titles over Spanish objectives, or the other way round."),
            ],
            Image: "story"),

        new PartGroup(
            "What the people around you say",
            "The talk you can walk past without stopping, and the talk you get when you do stop.",
            [
                // GoldSaucerTalk IS IN THIS BOX AND NOT IN THE INTERFACE ONE, which is where its
                // near-namesake went. GoldSaucerTextData is scoreboards and race courses — furniture.
                // This sheet is the Mini Cactpot crier working the crowd and the ticket seller
                // answering you, so it is somebody talking. The purchase prompt rides along because
                // it is the same conversation: you are told the price by the person selling.
                new TranslationPart(
                    "Talking to someone",
                    "The box that opens when you speak to somebody who has nothing to do with a "
                    + "quest, including the Gold Saucer's criers and the attendants who sell you a "
                    + "Cactpot ticket.",
                    ["defaulttalk", "goldsaucertalk"]),

                // THIS BOX IS HONESTLY TWO THINGS AND SAYS SO. custom/ is 26,859 rows over 752
                // conversations, and 83% of it is measurably people talking — but the service windows
                // are in there too, and they cannot be separated: CmnDefRetainerBell_00544 is 785
                // rows of menu entries and retainer small talk in ONE file. Naming the windows in the
                // description is the only honest option left, so it names them.
                //
                // CustomTalk is the list of verbs you pick from before any of it is spoken. It has to
                // travel with custom/ or the menu and the answer end up in different languages.
                new TranslationPart(
                    "\"Ask about...\" menus and service windows",
                    "The list of topics an NPC offers when they have several things to tell you, what "
                    + "they say once you pick one, and the windows you work in afterwards — the "
                    + "retainer, the aetheryte, the levequest board, linkshells, relic trade-ins and "
                    + "your estate.",
                    ["custom/", "customtalk"]),

                // One checkbox for two sheets, decided by looking rather than by reading their
                // names. Balloon leans towards pedlars' cries and idle city chatter, NpcYell towards
                // combat barks — but only by a lean: every lexical probe separates them by under 3%,
                // 265 strings appear in BOTH sheets word for word, and in testing every balloon that
                // could be found came from NpcYell. Two boxes whose difference nobody can see is a
                // worse answer than one box that covers what people mean.
                //
                // THE RED "SEALED OFF" BANNER IS NOT HERE AND THE DESCRIPTION NO LONGER CLAIMS IT.
                // It is LogMessage#2012 and #2013, so it belongs to the chat log box. Zero matches in
                // Balloon, NpcYell or InstanceContentTextData.
                new TranslationPart(
                    "Shouts and speech balloons",
                    "The balloons that appear over people's heads as you walk past them, and the "
                    + "warnings shouted during a fight.",
                    ["balloon", "npcyell"]),
            ],
            "By far the largest part of the translation. Switching this off is the biggest single "
            + "change you can make here."),

        // FIVE SHEETS IN ONE BOX, WHICH IS A MERGE AND NOT A SHRUG. They are the same kind of sheet:
        // everything one type of content produces — dialogue, objectives, mechanic cues and the
        // occasional scoreboard. Measured, the functional share is 10.7% of InstanceContentTextData
        // and 7.3% of PublicContentTextData, which is the same shape; a player told them apart by
        // name would be guessing.
        //
        // AND NONE OF IT IS AMBIENT, WHICH IS WHERE THEY NEARLY WENT. «Clear the binding lock» is an
        // objective, «It's beginning to crack!» a mechanic cue, and «Successful catches / Current
        // score» the Restoration's scoreboard. Filed as flavour, switching it off would quietly take
        // the objectives off the screen.
        new PartGroup(
            "Duties, raids and field operations",
            "What happens once you are inside a dungeon, a raid, or one of the large field zones.",
            [
                // VVDVoteRouteLabel TRAVELS WITH ContentTalk BECAUSE THEY HOLD THE SAME SENTENCE.
                // «Our first fork in the road. Where should we go?» is in both, and the variant
                // dungeon's voting window reads the VVD one — which is how it came to be showing
                // English while the Spanish sat in ContentTalk, translated and never used. Two
                // checkboxes for one window would let somebody switch off half a vote.
                new TranslationPart(
                    "Dialogue, objectives and on-screen text",
                    "What bosses and NPCs say while you are inside, the objectives that appear as it "
                    + "goes on, the route your party votes on in a variant dungeon, and the same for "
                    + "the large field zones and the big group content: Eureka, Bozja, Zadnor, the "
                    + "Occult Crescent, the Ishgardian Restoration and the Diadem.",
                    ["instancecontenttextdata", "contenttalk", "publiccontenttextdata",
                     "massivepccontenttextdata", "partycontenttextdata", "vvdvoteroutelabel"]),

                new TranslationPart(
                    "Objects and mechanisms",
                    "The text you get from levers, doors, corpses and the other things you can "
                    + "interact with inside a duty, and the signs and journals you stop to read — "
                    + "the Toto-Rak expedition notes among them.",
                    ["gimmicktalk", "gimmickbill"]),

                // WHAT ONLY EXISTS INSIDE ONE PIECE OF CONTENT, and it is a family rather than one
                // expansion's quirk: the deep dungeons have their pomanders and aetherpool gear, the
                // Occult Crescent its phantom jobs, traits and lore log, Eureka its aether items.
                // Named after the kind of thing rather than after any one duty, because the first
                // draft of this box was called "the Occult Crescent's own windows" and would have
                // needed renaming the moment the deep dungeon sheets were extracted a day later.
                new TranslationPart(
                    "The items, jobs and gear found only inside them",
                    "The things that exist in one piece of content and nowhere else: the pomanders "
                    + "and aetherpool weapons of the deep dungeons, the floor effects announced as "
                    + "you descend, and the Occult Crescent's phantom jobs, their traits, and the "
                    + "lore log it fills in as you explore.",
                    ["mkdsupportjob", "mkdtrait", "mkdlore", "deepdungeonitem",
                     "deepdungeonequipment", "deepdungeonflooreffectui", "deepdungeondemiclone",
                     "eurekaaetheritem"]),
            ]),

        // One box for the whole window, which is what it was asked to be: the roulettes and the
        // duties below them are one list to the person reading it, and there is no reading of "put
        // the Duty Finder in Spanish" that wants the roulette names left in English above
        // descriptions that are not. GuildOrder rides along because a guildhest's briefing is drawn
        // in the same panel — the pair of screenshots for it was taken in that window.
        //
        // Its own group rather than a part of the interface, because it is read beside the content it
        // describes. GoldSaucerTextData used to be bundled in here under a heading that said
        // "descriptions", which covered for it; the sheet is chocobo courses and scoreboards, so it
        // moved to the interface where it belongs.
        new PartGroup(
            "Duty Finder",
            "The blurbs that tell you what something is before you go into it.",
            [
                new TranslationPart(
                    "Duty Finder",
                    "The Duty Finder from top to bottom: the roulettes and what each one asks of you "
                    + "— including chocobo racing and ranked PvP — the paragraph down the right when "
                    + "you pick a dungeon, trial or raid, and the briefing a guildhest gives you as it "
                    + "starts.",
                    ["contentfinderconditiontransient", "contentroulette", "guildorder"]),
            ],
            Image: "duty"),

        new PartGroup(
            "Menus and interface",
            "The game's own furniture: window titles, tabs, buttons, and the labels next to your "
            + "numbers.",
            [
                // THREE SHEETS, ONE BOX, AND THE TWO PASSENGERS ARE HERE ON PURPOSE.
                //
                // RetainerTaskRandom is the venture names. They are read in the retainer window, and
                // that window IS `addon` — Addon#2377 is «Retainer: … Ventures: … Select an option.»,
                // all three lines in one row, with #2378-#2407 beneath it. Separately switchable, they
                // would let somebody turn off the venture list and keep the labels around it.
                //
                // GoldSaucerTextData is an `addon` for one content area: 84 rows of racing courses,
                // grades, placings and HUD counters — «Attacks Evaded», «Current MGP», «R-180». Same
                // job, smaller scope, so the same box. Its own would allow a Spanish scoreboard over
                // an English interface.
                // MainCommand IS THE MAIN MENU and joined this box on 16 August 2026. It had been
                // falling through to "Other text in this pack" under its own sheet name, which named
                // it after a file and filed the game's most-used menu with the leftovers.
                new TranslationPart(
                    "Menus, buttons and window titles",
                    "Everything written on the interface itself — the main menu you open with Esc, "
                    + "the Character window, the Duty Finder, your inventory, the retainer windows and "
                    + "their venture list, the Gold Saucer's scoreboards and race courses, the tabs "
                    + "across the top of a window and the buttons along the bottom.",
                    ["addon", "maincommand", "retainertaskrandom", "goldsaucertextdata"]),

                // TWO SHEETS AND THEY MUST SHARE A BOX, for the reason VVDVoteRouteLabel shares one
                // with ContentTalk: THEY HOLD THE SAME SENTENCE. «Radiant's Gear Augmentation
                // (IL 600)» is SpecialShop#1770447 and it is also TopicSelect#3276940, and the menu
                // the vendor opens reads the TopicSelect one. SpecialShop was translated first and
                // alone, and the window still came up in English — which is how this was found.
                // Two checkboxes would let somebody translate the shop and not the way in.
                new TranslationPart(
                    "Shop and exchange windows",
                    "The title on a vendor's window and the list of shops they offer before you pick "
                    + "one: the tomestone exchanges, the gear sets listed by item level, and the "
                    + "seasonal event stalls.",
                    ["specialshop", "topicselect"]),

                new TranslationPart(
                    "Title screen and character creation",
                    "The screens before you are in the world: logging in, choosing a character, and "
                    + "the races, clans and options you pick from when making one.",
                    ["lobby"]),
            ],
            "Many other Dalamud plugins and combat parsers look for these words in English and stop "
            + "working when they are not. This is the usual reason to switch something off here.\n\n"
            + "Translations also keep proper names in English inside sentences, to match the "
            + "interface you are reading them against. Translating the interface as well leaves "
            + "those names looking inconsistent until the two are brought in line.",
            "interface"),

        // TWO BOXES, AND THEY USED TO BE ONE. Active Help interrupts you the first time you do
        // something; a content guide is a window you go and open. DescriptionString alone is 1,576
        // rows with entries up to 3,258 characters — the whole mahjong rulebook, the Bozja briefing,
        // the Island Sanctuary guide — and nobody reading it thinks of it as a tutorial popup.
        new PartGroup(
            "Tutorials and guides",
            "The game explaining itself to you.",
            [
                // MultipleHelpString RIDES WITH THE HOW-TOS RATHER THAN GETTING A BOX OF ITS OWN. It
                // was given one on 16 August and merged the same day: the trigger differs — Active
                // Help interrupts you, this waits behind the "?" in a window's corner — but that is a
                // distinction about how the text arrives, not about what it is, and the table's rule
                // is that a checkbox names something the player can point at. 37 rows against 969 is
                // also not a split anybody would go looking for.
                new TranslationPart(
                    "Active Help and window help",
                    "The windows that pop up the first time you do something, the same texts again "
                    + "when you look them up from the main menu afterwards, and the help behind the "
                    + "question mark in the corner of a window — such as the Duty Finder's pages on "
                    + "registering for a duty and what happens next.",
                    ["howto", "howtopage", "howtocategory", "multiplehelpstring", "multiplehelp"]),

                new TranslationPart(
                    "Content guides",
                    "The written guide a content window opens: the rules of mahjong and Triple Triad, "
                    + "and the briefings for Bozja, deep dungeons, the Island Sanctuary and New "
                    + "Game+.",
                    // `descriptionstandalonetransient` names the guides reached from the main menu
                    // or from a window's own question mark, as opposed to the ones a content window
                    // opens. It belongs with these two and NOT with the how-tos: leave it out and
                    // the title above the page reads English while the page reads Spanish, which is
                    // exactly how it was found.
                    ["description", "descriptionstring", "descriptionstandalonetransient"]),
            ],
            Image: "help"),

        // One box for 8,469 rows a player would happily split and cannot. LogKind isolates the battle
        // log cleanly — kinds 41-49, 84 rows — but the duty announcements sit in a 4,495-row bucket
        // with party invites and system notices, so there is no line to cut on. Parked in WORKQUEUE.
        new PartGroup(
            "Combat log and system messages",
            "The lines the game writes into your chat log by itself.",
            [
                new TranslationPart(
                    "Combat log and system messages",
                    "Everything the game writes into your chat log by itself: what you hit and for "
                    + "how much, emotes, gil spent and earned, party and Free Company notices, "
                    + "gathering and crafting, market board messages, the announcements a duty makes "
                    + "including the red banner when a zone is sealed off, and every \"unable to\" the "
                    + "game answers with.",
                    ["logmessage"]),
            ],
            "Combat parsers and several plugins read these lines in English and will not recognise "
            + "them translated."),
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
