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
///     The English is the fallback beside each key; the other languages are <c>loc/&lt;code&gt;.json</c>.
/// </remarks>
internal static class PackParts
{
    /// <summary>Where sheets the table does not know end up.</summary>
    public static string OtherGroupName =>
        Loc.Localize("Group.Other.Name", "Other text in this pack");

    /// <summary>
    ///     The groups, in the order they are drawn.
    /// </summary>
    /// <remarks>
    ///     Matching is exact: a key not listed falls to the leftovers box, so a sheet is never
    ///     covered by a similarly named neighbour. A sheet appears under exactly one part.
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
            Loc.Localize("Group.Story.Name", "Quests and story"),
            Loc.Localize("Group.Story.Tooltip", "Text of the main scenario, side quests and cutscenes."),
            [
                new TranslationPart(
                    Loc.Localize("Part.QuestText.Name", "Quest dialogue, journal and objectives"),
                    Loc.Localize("Part.QuestText.Tooltip",
                        "Shows the text when you talk to NPCs about a quest, the story summaries in the "
                        + "journal, the list of steps in the tracker and the quest pop-up notices."),
                    [
                        "quest/",   // Quest dialogue, journal entries, tracker objectives and quest notices. One file per quest.
                        "leve/",    // Levequest dialogue and the levemete window.
                        "opening/", // Dialogue of the first NPC a new character meets.
                    ],
                    Image: "story"),

                new TranslationPart(
                    Loc.Localize("Part.Cutscenes.Name", "Cutscene subtitles"),
                    Loc.Localize("Part.Cutscenes.Tooltip",
                        "Translates the subtitle lines at the bottom of the screen during voiced cutscenes."),
                    [
                        "cut_scene/", // The voiced cutscenes.
                    ]),

                new TranslationPart(
                    Loc.Localize("Part.QuestNames.Name", "Quest names"),
                    Loc.Localize("Part.QuestNames.Tooltip",
                        "Translates only the quest titles (in the tracker, the journal and the Unending Journey)."),
                    [
                        "quest",           // Quest titles, as the journal and the quest list show them.
                        "completejournal", // The Unending Journey and the completed-quest log. Repeats the titles above.
                    ],
                    Loc.Localize("Part.QuestNames.Warning",
                        "Names and text are separate parts. With only one of them on, titles and "
                        + "objectives are shown in different languages.")),
            ]),

        new PartGroup(
            Loc.Localize("Group.People.Name", "Ambient dialogue and the world"),
            Loc.Localize("Group.People.Tooltip", "Casual conversations and flavour text around the map."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Talk.Name", "Talking to someone"),
                    Loc.Localize("Part.Talk.Tooltip",
                        "The text window that opens when you talk to NPCs with no quest (including "
                        + "Gold Saucer criers and vendors)."),
                    [
                        "defaulttalk",    // NPC dialogue in the Talk window with no quest behind it.
                        "goldsaucertalk", // Gold Saucer criers and the prompts before a purchase.
                    ]),

                new TranslationPart(
                    Loc.Localize("Part.AskAbout.Name", "\"Ask about...\" menus and information windows"),
                    Loc.Localize("Part.AskAbout.Tooltip",
                        "The dialogue options an NPC offers when you ask about several topics, and "
                        + "the explanatory text of each option."),
                    [
                        "custom/",    // The dialogue behind the menu, and the service windows it opens.
                        "customtalk", // The topics themselves. Without it they stay English over translated answers.
                    ]),

                new TranslationPart(
                    Loc.Localize("Part.Balloons.Name", "Speech balloons and shouts"),
                    Loc.Localize("Part.Balloons.Tooltip",
                        "Text balloons over characters' heads as you walk by, and warnings shouted in combat."),
                    [
                        "balloon", // The balloons over NPC heads.
                        "npcyell", // NPC shouts, in and out of combat.
                    ]),
            ],
            Loc.Localize("Group.People.Warning", "This is the largest part of the translation.")),

        new PartGroup(
            Loc.Localize("Group.Duties.Name", "Dungeons, raids and special zones"),
            Loc.Localize("Group.Duties.Tooltip",
                "Everything that happens inside combat instances and large-scale content zones."),
            [
                new TranslationPart(
                    Loc.Localize("Part.DutyText.Name", "Text and interactions inside content (Duties)"),
                    Loc.Localize("Part.DutyText.Tooltip",
                        "What bosses and NPCs say inside a dungeon or raid, on-screen objectives, route "
                        + "votes in Variant Dungeons and the records of zones such as Eureka, Bozja or "
                        + "the Occult Crescent."),
                    [
                        "instancecontenttextdata",  // Boss and NPC dialogue inside a dungeon, trial or raid.
                        "contenttalk",              // NPCs the player can speak to inside a duty.
                        "publiccontenttextdata",    // Dialogue and announcements in open-world content.
                        "massivepccontenttextdata", // Text from the large group content.
                        "partycontenttextdata",     // Text from party content.
                        "vvdvoteroutelabel",        // The variant dungeon route vote. The voting window reads this, not contenttalk.
                        "vvdnotebookcontents",      // The variant dungeon record entries.
                        "vvdnotebookseries",        // The headings of those entries.
                        "dungeon/",                 // Boss voices inside a dungeon.
                        "raid/",                    // The NPC at a raid entrance and the menu it opens.
                        "content/",                 // The NPCs at the bottom of the deep dungeons.
                    ]),

                new TranslationPart(
                    Loc.Localize("Part.Objects.Name", "Objects and information in the world"),
                    Loc.Localize("Part.Objects.Tooltip",
                        "What a mechanism or lever tells you when used, plus documents, letters, signs "
                        + "and puzzle hints."),
                    [
                        "gimmicktalk", // The message an object or mechanism gives when used.
                        "gimmickbill", // Signs, notes and puzzle hints read inside a duty.
                    ],
                    Image: "examine"),

                new TranslationPart(
                    Loc.Localize("Part.DeepDungeons.Name", "Deep Dungeon systems and unique content"),
                    Loc.Localize("Part.DeepDungeons.Tooltip",
                        "Elements exclusive to Palace of the Dead, Heaven-on-High and Eureka Orthos "
                        + "(pomanders, floor effects, demiclones) together with the field lore log."),
                    [
                        "deepdungeonitem",          // The pomanders and their descriptions.
                        "deepdungeonequipment",     // Aetherpool arm and armour.
                        "deepdungeonflooreffectui", // The floor effects and their descriptions.
                        "deepdungeondemiclone",     // The demiclones and their descriptions.
                        "eurekaaetheritem",         // Eureka aether items.
                        "mkdsupportjob",            // The Occult Crescent job selector: full name, HUD abbreviation and description.
                        "mkdtrait",                 // The trait list inside the phantom job window.
                        "mkdlore",                  // The field lore log.
                    ]),
            ]),

        new PartGroup(
            Loc.Localize("Group.Finder.Name", "Finder and guides (Duty Finder and tutorials)"),
            Loc.Localize("Group.Finder.Tooltip", "Instance names and in-game explanations."),
            [
                new TranslationPart(
                    Loc.Localize("Part.DutyFinder.Name", "Content finder (Duty Finder)"),
                    Loc.Localize("Part.DutyFinder.Tooltip",
                        "The name of every dungeon, hunt, trial and raid in the list, the roulette "
                        + "explanations and the short guildhest tactics."),
                    [
                        "contentfindercondition",          // The name of every dungeon, guildhest, trial and raid.
                        "contentfinderconditiontransient", // The description panel, one row per instance.
                        "contentroulette",                 // The roulettes, their short names and their descriptions.
                        "guildorder",                      // The guildhest objective and its three tactical hints.
                        "guild_order/",                    // The guildhest guide window.
                    ],
                    Image: "duty"),

                new TranslationPart(
                    Loc.Localize("Part.ActiveHelp.Name", "Tutorials and Active Help"),
                    Loc.Localize("Part.ActiveHelp.Tooltip",
                        "The help pop-ups when you do something for the first time, and the journal tutorials."),
                    [
                        "howto",                // The titles of the journal How-to tutorials.
                        "howtopage",            // The text of each tutorial page.
                        "howtocategory",        // The headings of those tutorials.
                        "eventtutorial",        // The titles of the tutorials an event opens.
                        "eventtutorialpage",    // The text of those tutorial pages.
                        "contentstutorial",     // The titles of the tutorials a content window opens.
                        "contentstutorialpage", // The text of those tutorial pages.
                        "multiplehelpstring",   // The topic in the list and the page it opens.
                        "multiplehelp",         // The titles of those help windows.
                    ],
                    Image: "help"),

                new TranslationPart(
                    Loc.Localize("Part.ContentGuides.Name", "Built-in content guides"),
                    Loc.Localize("Part.ContentGuides.Tooltip",
                        "Built-in manuals such as the rules of Mahjong, Triple Triad and the Island Sanctuary."),
                    [
                        "descriptionstring",              // The pages of the guide.
                        "description",                    // The window titles above those pages.
                        "descriptionstandalonetransient", // The names of the guides opened from the main menu.
                    ],
                    Image: "contentguide"),
            ]),

        new PartGroup(
            Loc.Localize("Group.Navigation.Name", "Navigation and compatibility"),
            Loc.Localize("Group.Navigation.Tooltip", "Names of aetherytes, Aethernet shards, ferries and travel menus."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Travel.Name", "Transport, aetherytes and travel names"),
                    Loc.Localize("Part.Travel.Tooltip",
                        "Names of aetherytes, Aethernet shards, ferries and travel menus. Lifestream "
                        + "looks these names up in English to know where to teleport you. Translated, "
                        + "navigation fails."),
                    [
                        "aetheryte",  // Aetherytes and Aethernet shards.
                        "transport/", // Ferries, chocobo porters and rental stables.
                        "warp/",      // The travel menu an aetheryte or an inn attendant opens.
                        "warp",       // The confirmation before a travel menu acts.
                        "eobjname",   // The name of every object the cursor can rest on. Singular and plural; the game picks by count.
                    ],
                    Image: "interactable"),
            ],
            Loc.Localize("Group.Navigation.Warning",
                "If you translate this, the related plugins stop working (example: Lifestream).")),

        new PartGroup(
            Loc.Localize("Group.SafeInterface.Name", "Safe interface and settings"),
            Loc.Localize("Group.SafeInterface.Tooltip", "General visual elements that break no script."),
            [
                new TranslationPart(
                    Loc.Localize("Part.MainMenu.Name", "Main menu and start screen"),
                    Loc.Localize("Part.MainMenu.Tooltip",
                        "The options of the menu that opens with Esc, the title screen when the game "
                        + "opens, and character creation."),
                    [
                        "maincommand",         // Every entry of the main menu, with its tooltip.
                        "maincommandcategory", // The seven headings of that menu.
                        "retainertaskrandom",  // The explorer venture names.
                        "goldsaucertextdata",  // Scoreboards and race courses.
                        "lobby",               // Title screen and character creation. Drawn before the player logs in.
                    ],
                    Image: "mainmenus"),
            ]),

        new PartGroup(
            Loc.Localize("Group.Interface.Name", "Interface critical for addons"),
            Loc.Localize("Group.Interface.Tooltip",
                "Window titles, buttons, confirmations, shop windows and loot windows."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Interface.Name", "Buttons, windows and interface (UI)"),
                    Loc.Localize("Part.Interface.Tooltip",
                        "Window titles, buttons, \"Yes/No\" confirmations, tomestone and exchange shop "
                        + "menus, and loot and coffer windows. Several plugins read the windows and "
                        + "buttons in English to interact with the interface. Translating them stops "
                        + "them from detecting those options."),
                    [
                        "addon",                 // Every button, tab, column heading and error the game draws.
                        "gimmickyesno",          // The Yes/No prompt an object shows before it acts.
                        "specialshop",           // The name of each shop window.
                        "topicselect",           // The vendor list of shops. Same strings as specialshop; the menu draws this one.
                        "inclusionshopcategory", // The two dropdowns at the top of an Item Exchange window.
                        "treasure",              // The coffers a duty leaves behind, and the loot window rows.
                        "shop/",                 // The titles and buttons of the exchange counters.
                    ],
                    Image: "interface"),
            ],
            Loc.Localize("Group.Interface.Warning",
                "If you translate this, the related plugins stop working (examples: Yes Already, "
                + "AutoRetainer, Pandora, LazyLoot, Deliveroo).")),

        new PartGroup(
            Loc.Localize("Group.Chat.Name", "Chat, messages and items"),
            Loc.Localize("Group.Chat.Tooltip", "On-screen logs and inventory information."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Log.Name", "Combat and system log"),
                    Loc.Localize("Part.Log.Tooltip",
                        "Everything that appears in the chat window (damage, emotes, gil notices, "
                        + "system errors, zone announcements)."),
                    [
                        "logmessage", // The whole chat log. One LogKind bucket mixes duty announcements with party invites.
                        "error",      // The system errors.
                        "system/",    // The prompt shown at a boundary and its buttons.
                        "story/",     // One boundary prompt.
                    ],
                    Loc.Localize("Part.Log.Warning",
                        "Combat parsers and several plugins read these lines in English.")),

                new TranslationPart(
                    Loc.Localize("Part.Items.Name", "Items and character attributes"),
                    Loc.Localize("Part.Items.Tooltip",
                        "The names of every item in the bags and on the market board, and the names "
                        + "and descriptions of attributes (Strength, Critical Hit, etc.)."),
                    [
                        "item",             // Every item: the singular and plural the <ennoun> macro reads, and the tooltip name.
                        "baseparam",        // The attributes and their tooltips. The names are also in addon, which draws the column.
                        "itemspecialbonus", // The heading a tooltip puts over a conditional bonus.
                    ]),
            ]),
    ];

    // Sheets with no string column, so never in a pack: switchtalk, tinycustomtalk.

    /// <summary>Every sheet key the table names, for telling the known from the unknown.</summary>
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
