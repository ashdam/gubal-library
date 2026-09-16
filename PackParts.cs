using CheapLoc;

namespace GubalLibrary;

internal sealed record TranslationPart(
    string Name, string Description, string[] Sheets, string? Warning = null, string? Image = null);

internal sealed record PartGroup(
    string Name, string Description, TranslationPart[] Parts, string? Warning = null, string? Image = null);

internal static class PackParts
{
    public static string OtherGroupName =>
        Loc.Localize("Group.Other.Name", "Other text in this pack");

    // Each sheet belongs to one part. Menu families include their dialogue because pages are served whole.
    public static PartGroup[] Groups => groups ??= Build();

    private static PartGroup[]? groups;

    public static void Invalidate() => groups = null;

    private static PartGroup[] Build() =>
    [
        new PartGroup(
            Loc.Localize("Group.Story.Name", "Quests and story"),
            Loc.Localize("Group.Story.Tooltip", "Quest dialogue, objectives, titles and cutscene subtitles."),
            [
                new TranslationPart(
                    Loc.Localize("Part.QuestText.Name", "Quest dialogue and objectives"),
                    Loc.Localize("Part.QuestText.Tooltip", "Quest conversations, journal entries, objectives and levequest descriptions."),
                    [
                        "quest/", "opening/", "leve",
                    ],
                    Image: "story"),
                new TranslationPart(
                    Loc.Localize("Part.QuestNames.Name", "Quest names"),
                    Loc.Localize("Part.QuestNames.Tooltip", "Translates only the quest titles (in the tracker, the journal and the Unending Journey)."),
                    [
                        "quest", "completejournal",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.Cutscenes.Name", "Cutscene subtitles"),
                    Loc.Localize("Part.Cutscenes.Tooltip", "Translates the subtitle lines at the bottom of the screen during voiced cutscenes."),
                    [
                        "cut_scene/",
                    ])
            ]),

        new PartGroup(
            Loc.Localize("Group.World.Name", "Dialogue and world lore"),
            Loc.Localize("Group.World.Tooltip", "Conversations, speech balloons, readable objects and lore records."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Talk.Name", "Ambient conversations and shouts"),
                    Loc.Localize("Part.Talk.Tooltip", "NPC dialogue outside quests, speech balloons and shouts, including combat shouts. NPC service conversations are under Interface menus."),
                    [
                        "defaulttalk", "balloon", "npcyell",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.Objects.Name", "Objects and information in the world"),
                    Loc.Localize("Part.Objects.Tooltip", "What a mechanism or lever tells you when used, plus documents, letters, signs and puzzle hints."),
                    [
                        "gimmicktalk", "gimmickbill",
                    ],
                    Image: "examine"),
                new TranslationPart(
                    Loc.Localize("Part.Lore.Name", "Codex and exploration records"),
                    Loc.Localize("Part.Lore.Tooltip", "The Unending Codex, Occult Record and Variant Dungeon records."),
                    [
                        "akatsukinotestring", "mkdlore", "vvdnotebookcontents", "vvdnotebookseries",
                    ],
                    Image: "codex")
            ]),

        new PartGroup(
            Loc.Localize("Group.Names.Name", "Names in the world"),
            Loc.Localize("Group.Names.Tooltip", "Names shown on maps and on characters and objects."),
            [
                new TranslationPart(
                    Loc.Localize("Part.PlaceNames.Name", "Place names"),
                    Loc.Localize("Part.PlaceNames.Tooltip", "Names of regions, zones and locations on maps and inside dungeons and other instances."),
                    [
                        "placename",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.TargetNames.Name", "Creatures and interactable objects"),
                    Loc.Localize("Part.TargetNames.Tooltip", "Names of enemies, pets, aetherytes and objects you can select. Travel menus are under Interface menus."),
                    [
                        "bnpcname", "pet", "aetheryte", "eobjname",
                    ],
                    Warning: Loc.Localize("Part.TargetNames.Warning", "Plugins that search for English object names may fail to find translated targets."),
                    Image: "interactable")
            ]),

        new PartGroup(
            Loc.Localize("Group.Combat.Name", "Combat and equipment"),
            Loc.Localize("Group.Combat.Tooltip", "Actions, effects, attributes and item information."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Actions.Name", "Jobs, actions and status effects"),
                    Loc.Localize("Part.Actions.Tooltip", "Job names, action names and descriptions, traits and status effects."),
                    [
                        "classjob", "action", "actiontransient", "trait", "traittransient",
                        "status",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.Items.Name", "Items and character attributes"),
                    Loc.Localize("Part.Items.Tooltip", "The names of every item in the bags and on the market board, and the names and descriptions of attributes (Strength, Critical Hit, etc.)."),
                    [
                        "item", "baseparam", "itemspecialbonus",
                    ])
            ]),

        new PartGroup(
            Loc.Localize("Group.Duties.Name", "Duties and field operations"),
            Loc.Localize("Group.Duties.Tooltip", "Duty objectives, dialogue and special combat systems."),
            [
                new TranslationPart(
                    Loc.Localize("Part.DutyText.Name", "Duty dialogue and objectives"),
                    Loc.Localize("Part.DutyText.Tooltip", "Dialogue, announcements and objectives in duties, FATEs and field operations, plus Variant Dungeon route choices."),
                    [
                        "instancecontenttextdata", "contenttalk", "publiccontenttextdata", "massivepccontenttextdata", "partycontenttextdata",
                        "vvdvoteroutelabel", "dungeon/", "fateevent", "guildorder",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.DutyFinder.Name", "Duty names and descriptions"),
                    Loc.Localize("Part.DutyFinder.Tooltip", "Duty Finder entries, roulette names and descriptions. Entry NPC menus are under Interface menus."),
                    [
                        "contentfindercondition", "contentfinderconditiontransient", "contentroulette",
                    ],
                    Image: "duty"),
                new TranslationPart(
                    Loc.Localize("Part.DeepDungeons.Name", "Deep Dungeon items and effects"),
                    Loc.Localize("Part.DeepDungeons.Tooltip", "Pomanders, aetherpool equipment, floor effects, demiclones and incense."),
                    [
                        "deepdungeonitem", "deepdungeonequipment", "deepdungeonflooreffectui", "deepdungeondemiclone",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.FieldSystems.Name", "Field operation jobs and equipment"),
                    Loc.Localize("Part.FieldSystems.Tooltip", "Phantom jobs and traits in the Occult Crescent, and Eureka aether items."),
                    [
                        "eurekaaetheritem", "mkdsupportjob", "mkdtrait",
                    ])
            ]),

        new PartGroup(
            Loc.Localize("Group.Activities.Name", "Other activities"),
            Loc.Localize("Group.Activities.Tooltip", "Minigames, crafting deliveries and Beastmaster content."),
            [
                new TranslationPart(
                    Loc.Localize("Part.GoldSaucer.Name", "Gold Saucer and minigames"),
                    Loc.Localize("Part.GoldSaucer.Tooltip", "Text for GATEs, arcade games, chocobo racing, Triple Triad cards, Lord of Verminion and Doman Mahjong."),
                    [
                        "goldsaucertextdata", "goldsaucerarcademachine", "rideshootingtextdata", "gfateclimbing", "gfatestelth",
                        "emjaddon", "chocoboraceability", "chocoboracechallenge", "chocoboraceitem", "racingchocobonamecategory",
                        "racingchocoboparam", "minionrace", "minionrules", "minionskilltype", "minionstage",
                        "tripletriadcard", "tripletriadcardtype", "tripletriadcompetition", "tripletriadrule", "goldsaucertalk",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.Crafting.Name", "Crafting and gathering deliveries"),
                    Loc.Localize("Part.Crafting.Tooltip", "Delivery requirements for the Crystalline Mean, Studium and Wachumeqimeqi."),
                    [
                        "bankacraftworks", "hugecraftworksnpc", "sharlayancraftworks",
                    ]),
                new TranslationPart(
                    Loc.Localize("Part.Beastmaster.Name", "Beastmaster and the Crucible"),
                    Loc.Localize("Part.Beastmaster.Tooltip", "Beast descriptions, Crucible equipment, action properties and score objectives."),
                    [
                        "xbmactioneffecttype", "xbmactiontarget", "xbmelement", "xbmitem", "xbmitemtype",
                        "xbmpet", "xbmscorebonus", "xbmscorerank",
                    ])
            ]),

        new PartGroup(
            Loc.Localize("Group.Guides.Name", "Tutorials and guides"),
            Loc.Localize("Group.Guides.Tooltip", "Help pages and explanations of game systems."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Guides.Name", "Tutorials and game manuals"),
                    Loc.Localize("Part.Guides.Tooltip", "Active Help, job gauge guides and built-in manuals for combat, housing, minigames and exploration."),
                    [
                        "howto", "howtopage", "howtocategory", "eventtutorial", "eventtutorialpage",
                        "contentstutorial", "contentstutorialpage", "multiplehelpstring", "multiplehelp", "descriptionstring",
                        "description", "descriptionstandalonetransient", "guidepagestring", "guidetitle",
                    ],
                    Image: "help")
            ]),

        new PartGroup(
            Loc.Localize("Group.Interface.Name", "Interface menus"),
            Loc.Localize("Group.Interface.Tooltip", "Windows, settings, shops and NPC services in one part."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Interface.Name", "Interface menus and NPC services"),
                    Loc.Localize("Part.Interface.Tooltip", "Buttons, settings, shops, retainers, travel and duty entry menus. Includes NPC topic menus and their dialogue and lore answers because each sheet is switched as a whole."),
                    [
                        "addon", "mcguffinuidata", "csbonustextdata", "contenttype", "gimmickyesno",
                        "specialshop", "gilshop", "disposalshop", "disposalshopfiltertype", "fccshop",
                        "inclusionshop", "inclusionshopwelcomtext", "gcshopitemcategory", "collectablesshop", "collectablesshopitemgroup",
                        "itemsearchcategory", "itemuicategory", "cabinetsubcategory", "journalcategory", "journalgenre",
                        "journalsection", "contentsnotecategory", "notebookdivisioncategory", "leveassignmenttype", "relicnotecategory",
                        "classjobactionuicategory", "contentuicategory", "onlinestatus", "circleactivity", "fcchestname",
                        "fchierarchy", "fcprofile", "fcreputation", "fcrights", "gcrankgridaniafemaletext",
                        "gcrankgridaniamaletext", "gcranklimsafemaletext", "gcranklimsamaletext", "gcrankuldahfemaletext", "gcrankuldahmaletext",
                        "housingappeal", "housingmateauthority", "housingmerchantpose", "housingplacement", "housingunplacement",
                        "furniturecatalogcategory", "yardcatalogcategory", "companycraftdraftcategory", "companycraftmanufactorystate", "companycrafttype",
                        "emotecategory", "orchestrioncategory", "playersearchlocation", "playersearchsublocation", "weather",
                        "stain", "topicselect", "inclusionshopcategory", "treasure", "shop/",
                        "maincommand", "maincommandcategory", "lobby", "retainertaskrandom", "custom/",
                        "customtalk", "raid/", "content/", "guild_order/", "leve/",
                        "guildleveassignmenttalk", "transport/", "warp/", "warp", "system/",
                        "story/", "grandcompany",
                    ],
                    Warning: Loc.Localize("Part.Interface.Warning", "Plugins that read English buttons or menus may not recognize translated options. This also affects travel and NPC service automation."),
                    Image: "interface")
            ]),

        new PartGroup(
            Loc.Localize("Group.Chat.Name", "Chat and system messages"),
            Loc.Localize("Group.Chat.Tooltip", "Combat logs, notifications and error messages."),
            [
                new TranslationPart(
                    Loc.Localize("Part.Log.Name", "Chat and system messages"),
                    Loc.Localize("Part.Log.Tooltip", "Combat messages, emotes, invitations, system notices and connection errors."),
                    [
                        "logmessage", "error",
                    ],
                    Warning: Loc.Localize("Part.Log.Warning", "Combat parsers and plugins may depend on English log messages."))
            ])
    ];

    private static readonly HashSet<string> Known =
        new(Groups.SelectMany(g => g.Parts).SelectMany(p => p.Sheets), StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string sheet) => Known.Contains(sheet);

    // Nested families retain the slash to distinguish Quest titles from quest/ dialogue.
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

        // Remove the language suffix before the page row number.
        if (end > 1 && tokens[end - 1].Length <= 4 && tokens[end - 1].All(char.IsAsciiLetter))
        {
            end--;
        }

        // Remove the page row number.
        if (end > 1 && tokens[end - 1].All(char.IsAsciiDigit))
        {
            end--;
        }

        return string.Join('_', tokens[..end]).ToLowerInvariant();
    }
}
