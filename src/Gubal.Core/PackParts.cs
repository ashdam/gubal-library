using Loc = GubalLibrary.CoreLanguage;

namespace GubalLibrary;

internal sealed record TranslationPart(
    string Id, string Name, string Description, string[] Sheets, string? Warning = null, string? Image = null,
    string[]? AdditionalImages = null);

internal sealed record PartGroup(
    string Id, string Name, string Description, TranslationPart[] Parts, string? Warning = null, string? Image = null);

internal static class PackParts
{
    public static string UncategorizedGroupName =>
        Loc.Localize("Group.Other.Name", "Uncategorized");

    // Each sheet belongs to one part. Menu families include their dialogue because pages are served whole.
    public static PartGroup[] Groups => groups ??= Build();

    private static PartGroup[]? groups;

    public static void Invalidate() => groups = null;

    private static PartGroup[] Build() =>
    [
        new PartGroup(
            "story",
            Loc.Localize("Group.Story.Name", "Quests"),
            Loc.Localize("Group.Story.Tooltip", "Quest dialogue, objectives, titles and cutscene subtitles."),
            [
                new TranslationPart(
                    "questtext",
                    Loc.Localize("Part.QuestText.Name", "MSQ and Quest dialogs and objectives"),
                    Loc.Localize("Part.QuestText.Tooltip", "Quest conversations, journal entries and objectives."),
                    [
                        "quest/", "opening/",
                    ],
                    Image: "story"),
                new TranslationPart(
                    "questnames",
                    Loc.Localize("Part.QuestNames.Name", "Quest names"),
                    Loc.Localize("Part.QuestNames.Tooltip", "Translates only the quest titles (in the tracker, the journal and the Unending Journey)."),
                    [
                        "quest", "completejournal",
                    ],
                    Image: "quest-names"),
                new TranslationPart(
                    "cutscenes",
                    Loc.Localize("Part.Cutscenes.Name", "Cutscene subtitles"),
                    Loc.Localize("Part.Cutscenes.Tooltip", "Translates the subtitle lines at the bottom of the screen during voiced cutscenes."),
                    [
                        "cut_scene/",
                    ],
                    Image: "cutscenes")
            ]),

        new PartGroup(
            "world",
            Loc.Localize("Group.World.Name", "Dialogue and world lore"),
            Loc.Localize("Group.World.Tooltip", "Conversations, speech balloons and readable objects."),
            [
                new TranslationPart(
                    "talk",
                    Loc.Localize("Part.Talk.Name", "Ambient conversations and shouts"),
                    Loc.Localize("Part.Talk.Tooltip", "NPC dialogue outside quests, speech balloons and shouts, including combat shouts. NPC service conversations are under Interface menus."),
                    [
                        "defaulttalk", "balloon", "npcyell",
                    ],
                    Image: "NPCbubbles", AdditionalImages: ["smalltalk", "instance-dialogue"]),
                new TranslationPart(
                    "objects",
                    Loc.Localize("Part.Objects.Name", "Objects and information in the world"),
                    Loc.Localize("Part.Objects.Tooltip", "What a mechanism or lever tells you when used, plus documents, letters, signs and puzzle hints."),
                    [
                        "gimmicktalk", "gimmickbill",
                    ],
                    Image: "examine")
            ]),

        new PartGroup(
            "places",
            Loc.Localize("Group.Places.Name", "Place names"),
            Loc.Localize("Group.Places.Tooltip", "Names shown on maps and inside instances."),
            [
                new TranslationPart(
                    "placenames",
                    Loc.Localize("Part.PlaceNames.Name", "Place names"),
                    Loc.Localize("Part.PlaceNames.Tooltip", "Names of regions, zones and locations on maps and inside dungeons and other instances."),
                    [
                        "placename",
                    ],
                    Image: "placename")
            ]),

        new PartGroup(
            "names",
            Loc.Localize("Group.Names.Name", "Floating names"),
            Loc.Localize("Group.Names.Tooltip", "Names shown on characters and objects."),
            [
                new TranslationPart(
                    "nonbattlenpcnames",
                    Loc.Localize("Part.NonBattleNpcNames.Name", "Non-battle NPC names"),
                    Loc.Localize("Part.NonBattleNpcNames.Tooltip", "Names and titles of story characters, merchants and other NPCs used for story events and interaction."),
                    [
                        "enpcresident",
                    ],
                    Warning: Loc.Localize("Part.TargetNames.Warning", "Plugins that search for English object names may fail to find translated targets."),
                    Image: "enpcname"),
                new TranslationPart(
                    "battlenpcnames",
                    Loc.Localize("Part.BattleNpcNames.Name", "Battle NPC names"),
                    Loc.Localize("Part.BattleNpcNames.Tooltip", "Names of monsters, bosses and other combat NPCs, including allies and training dummies."),
                    [
                        "bnpcname",
                    ],
                    Warning: Loc.Localize("Part.TargetNames.Warning", "Plugins that search for English object names may fail to find translated targets."),
                    Image: "bnpcname"),
                new TranslationPart(
                    "targetnames",
                    Loc.Localize("Part.TargetNames.Name", "Creatures and interactable objects"),
                    Loc.Localize("Part.TargetNames.Tooltip", "Names of pets, aetherytes, gathering points and objects you can select. Travel menus are under Interface menus."),
                    [
                        "pet", "aetheryte", "eobjname", "gatheringpointname",
                    ],
                    Warning: Loc.Localize("Part.TargetNames.Warning", "Plugins that search for English object names may fail to find translated targets."),
                    Image: "interactable")
            ]),

        new PartGroup(
            "combat",
            Loc.Localize("Group.Combat.Name", "Combat"),
            Loc.Localize("Group.Combat.Tooltip", "Jobs, actions, traits, status effects and character attributes."),
            [
                new TranslationPart(
                    "jobs",
                    Loc.Localize("Part.Jobs.Name", "Class and job names"),
                    Loc.Localize("Part.Jobs.Tooltip", "Class and job names throughout the game, including combat jobs, Disciples of the Hand and Disciples of the Land."),
                    ["classjob"],
                    Image: "jobs"),
                new TranslationPart(
                    "actions",
                    Loc.Localize("Part.Actions.Name", "Actions"),
                    Loc.Localize("Part.Actions.Tooltip", "Action names, descriptions and categories, including crafting and gathering actions."),
                    ["action", "actiontransient", "actioncategory"],
                    Image: "actions"),
                new TranslationPart(
                    "traits",
                    Loc.Localize("Part.Traits.Name", "Traits"),
                    Loc.Localize("Part.Traits.Tooltip", "Trait names and descriptions."),
                    ["trait", "traittransient"]),
                new TranslationPart(
                    "status",
                    Loc.Localize("Part.Status.Name", "Status effects"),
                    Loc.Localize("Part.Status.Tooltip", "Status effect names and descriptions, including free company actions."),
                    ["status", "companyaction"],
                    Image: "status"),
                new TranslationPart(
                    "attributes",
                    Loc.Localize("Part.Attributes.Name", "Character attributes"),
                    Loc.Localize("Part.Attributes.Tooltip", "Names and descriptions of character attributes throughout the game, such as Strength and Critical Hit. This setting affects all uses of these attributes, not only combat."),
                    [
                        "baseparam",
                    ],
                    Image: "baseparam")
            ]),

        new PartGroup(
            "items",
            Loc.Localize("Group.Items.Name", "Items"),
            Loc.Localize("Group.Items.Tooltip", "Item names and descriptions, including quest items and special activity items."),
            [
                new TranslationPart(
                    "items",
                    Loc.Localize("Part.Items.Name", "Items"),
                    Loc.Localize("Part.Items.Tooltip", "Item names and descriptions, including quest items, Deep Dungeon items, Eureka items, chocobo race items and Beastmaster items."),
                    [
                        "item", "eventitem", "eventitemhelp", "eventitemcategory", "itemspecialbonus",
                        "deepdungeonitem", "deepdungeonequipment", "deepdungeondemiclone",
                        "eurekaaetheritem", "chocoboraceitem", "xbmitem", "xbmitemtype",
                    ])
            ]),

        new PartGroup(
            "duties",
            Loc.Localize("Group.Duties.Name", "Duties and field operations"),
            Loc.Localize("Group.Duties.Tooltip", "Duty objectives, dialogue and special combat systems."),
            [
                new TranslationPart(
                    "dutytext",
                    Loc.Localize("Part.DutyText.Name", "Duty dialogue and objectives"),
                    Loc.Localize("Part.DutyText.Tooltip", "Dialogue, announcements and objectives in duties, FATEs and field operations, plus Variant Dungeon route choices."),
                    [
                        "instancecontenttextdata", "contenttalk", "publiccontenttextdata", "massivepccontenttextdata", "partycontenttextdata",
                        "vvdvoteroutelabel", "dungeon/", "fateevent", "guildorder", "levestring",
                    ]),
                new TranslationPart(
                    "dutyfinder",
                    Loc.Localize("Part.DutyFinder.Name", "Duty names and descriptions"),
                    Loc.Localize("Part.DutyFinder.Tooltip", "Duty Finder entries and names and descriptions of roulettes, levequests, FATEs and critical engagements. Entry NPC menus are under Interface menus."),
                    [
                        "contentfindercondition", "contentfinderconditiontransient", "contentroulette", "leve", "fate", "dynamicevent", "dynamiceventenemytype",
                    ],
                    Image: "duty"),
                new TranslationPart(
                    "deepdungeons",
                    Loc.Localize("Part.DeepDungeons.Name", "Deep Dungeon effects"),
                    Loc.Localize("Part.DeepDungeons.Tooltip", "Special effects applied to Deep Dungeon floors."),
                    [
                        "deepdungeonflooreffectui",
                    ]),
                new TranslationPart(
                    "fieldsystems",
                    Loc.Localize("Part.FieldSystems.Name", "Field operation jobs and actions"),
                    Loc.Localize("Part.FieldSystems.Tooltip", "Phantom jobs and traits in the Occult Crescent, and lost action categories in Bozja."),
                    [
                        "mkdsupportjob", "mkdtrait", "myctemporaryitemuicategory",
                    ])
            ]),

        new PartGroup(
            "guides",
            Loc.Localize("Group.Guides.Name", "Tutorials and guides"),
            Loc.Localize("Group.Guides.Tooltip", "Help pages and explanations of game systems."),
            [
                new TranslationPart(
                    "guides",
                    Loc.Localize("Part.Guides.Name", "Tutorials and game manuals"),
                    Loc.Localize("Part.Guides.Tooltip", "Active Help, job gauge guides and built-in manuals for combat, housing, minigames and exploration."),
                    [
                        "howto", "howtopage", "howtocategory", "eventtutorial", "eventtutorialpage",
                        "contentstutorial", "contentstutorialpage", "multiplehelpstring", "multiplehelp", "descriptionstring",
                        "description", "descriptionstandalonetransient", "guidepagestring", "guidetitle",
                    ],
                    Image: "help", AdditionalImages: ["contentguide"])
            ]),

        new PartGroup(
            "interface",
            Loc.Localize("Group.Interface.Name", "Interface menus"),
            Loc.Localize("Group.Interface.Tooltip", "Title screen, main menus, windows and NPC services."),
            [
                new TranslationPart(
                    "lobby",
                    Loc.Localize("Part.Lobby.Name", "Lobby"),
                    Loc.Localize("Part.Lobby.Tooltip", "Title screen, character selection and character creation."),
                    ["lobby"],
                    Image: "lobby"),
                new TranslationPart(
                    "mainmenus",
                    Loc.Localize("Part.MainMenus.Name", "Main menus"),
                    Loc.Localize("Part.MainMenus.Tooltip", "Main menu entries and categories."),
                    ["maincommand", "maincommandcategory"],
                    Warning: Loc.Localize("Part.MainMenus.Warning", "Plugins that read English menu entries may not recognize translated options."),
                    Image: "mainmenus"),
                new TranslationPart(
                    "emotes",
                    Loc.Localize("Part.Emotes.Name", "Emotes"),
                    Loc.Localize("Part.Emotes.Tooltip", "Emote names, descriptions and categories in the emote menu. This setting does not affect commands typed in chat."),
                    ["emote", "emotecategory"]),
                new TranslationPart(
                    "interface",
                    Loc.Localize("Part.Interface.Name", "Other interface menus and NPC services"),
                    Loc.Localize("Part.Interface.Tooltip", "Buttons, settings, shops, retainers, free companies, housing, gathering, travel and duty entry menus. Includes NPC topic menus and their dialogue and lore answers because each sheet is switched as a whole."),
                    [
                        "addon", "mcguffinuidata", "csbonustextdata", "contenttype", "gimmickyesno", "eventaction",
                        "specialshop", "gilshop", "disposalshop", "disposalshopfiltertype", "fccshop",
                        "inclusionshop", "inclusionshopwelcomtext", "gcshopitemcategory", "collectablesshop", "collectablesshopitemgroup",
                        "itemsearchcategory", "itemuicategory", "cabinetsubcategory", "journalcategory", "journalgenre",
                        "journalsection", "crafttype", "leveassignmenttype", "notebookdivision", "recipesubcategory", "secretrecipebook",
                        "gatheringtype", "questredochapteruicategory",
                        "classjobactionuicategory", "contentuicategory", "onlinestatus", "circleactivity", "fcchestname",
                        "fchierarchy", "fcprofile", "fcreputation", "fcrights", "gcrankgridaniafemaletext",
                        "gcrankgridaniamaletext", "gcranklimsafemaletext", "gcranklimsamaletext", "gcrankuldahfemaletext", "gcrankuldahmaletext",
                        "housingappeal", "housingmateauthority", "housingmerchantpose", "housingplacement", "housingunplacement",
                        "furniturecatalogcategory", "yardcatalogcategory", "companycraftdraftcategory", "companycraftmanufactorystate", "companycrafttype",
                        "orchestrioncategory", "playersearchlocation", "playersearchsublocation", "weather",
                        "stain", "topicselect", "inclusionshopcategory", "treasure", "shop/",
                        "retainertaskrandom", "custom/",
                        "customtalk", "raid/", "content/", "guild_order/", "leve/",
                        "guildleveassignmenttalk", "transport/", "warp/", "warp", "system/",
                        "story/", "grandcompany",
                        "aquariumwater", "fcactivity", "fcactivitycategory", "fcauthority", "fcauthoritycategory",
                        "gatheringcondition", "gatheringpointbonustype", "mateauthoritycategory", "platform", "pointmenustring", "warplogic",
                    ],
                    Warning: Loc.Localize("Part.Interface.Warning", "Plugins that read English buttons or menus may not recognize translated options. This also affects travel and NPC service automation."),
                    Image: "interface")
            ]),

        new PartGroup(
            "chat",
            Loc.Localize("Group.Chat.Name", "Chat and system messages"),
            Loc.Localize("Group.Chat.Tooltip", "Chat messages, the auto-translate dictionary, text commands, notifications and errors."),
            [
                new TranslationPart(
                    "log",
                    Loc.Localize("Part.Log.Name", "Chat and system messages"),
                    Loc.Localize("Part.Log.Tooltip", "Combat messages, emotes, invitations, system notices, connection errors and the auto-translate dictionary."),
                    [
                        "logmessage", "error", "completion",
                    ],
                    Warning: Loc.Localize("Part.Log.Warning", "Combat parsers and plugins may depend on English log messages."),
                    Image: "logmessage"),
                new TranslationPart(
                    "textcommands",
                    Loc.Localize("Part.TextCommands.Name", "Text commands"),
                    Loc.Localize("Part.TextCommands.Tooltip", "System and emote commands typed in chat."),
                    ["textcommand"])
            ]),

        new PartGroup(
            "misc",
            Loc.Localize("Group.Misc.Name", "Other"),
            Loc.Localize("Group.Misc.Tooltip", "Minigames, crafting deliveries, Beastmaster content, achievements, logs and codex entries."),
            [
                new TranslationPart(
                    "goldsaucer",
                    Loc.Localize("Part.GoldSaucer.Name", "Gold Saucer and minigames"),
                    Loc.Localize("Part.GoldSaucer.Tooltip", "Text for GATEs, arcade games, chocobo racing, Triple Triad cards, Lord of Verminion and Doman Mahjong."),
                    [
                        "goldsaucertextdata", "goldsaucerarcademachine", "rideshootingtextdata", "gfateclimbing", "gfatestelth",
                        "emjaddon", "fgsaddon", "chocoboraceability", "chocoboracechallenge", "racingchocobonamecategory",
                        "racingchocoboparam", "minionrace", "minionrules", "minionskilltype", "minionstage",
                        "tripletriadcard", "tripletriadcardtype", "tripletriadcompetition", "tripletriadrule", "goldsaucertalk",
                    ]),
                new TranslationPart(
                    "crafting",
                    Loc.Localize("Part.Crafting.Name", "Crafting and gathering deliveries"),
                    Loc.Localize("Part.Crafting.Tooltip", "Delivery requirements for the Crystalline Mean, Studium and Wachumeqimeqi."),
                    [
                        "bankacraftworks", "hugecraftworksnpc", "sharlayancraftworks",
                    ]),
                new TranslationPart(
                    "beastmaster",
                    Loc.Localize("Part.Beastmaster.Name", "Beastmaster and the Crucible"),
                    Loc.Localize("Part.Beastmaster.Tooltip", "Beast descriptions, action properties and score objectives."),
                    [
                        "xbmactioneffecttype", "xbmactiontarget", "xbmelement",
                        "xbmpet", "xbmscorebonus", "xbmscorerank",
                    ]),
                new TranslationPart(
                    "achievements",
                    Loc.Localize("Part.Achievements.Name", "Achievements"),
                    Loc.Localize("Part.Achievements.Tooltip", "Category names in the achievements menu."),
                    ["achievementkind"]),
                new TranslationPart(
                    "logs",
                    Loc.Localize("Part.Logs.Name", "Logs"),
                    Loc.Localize("Part.Logs.Tooltip", "Sightseeing Log, Challenge Log, Wondrous Tails, crafting and gathering log categories, relic book categories and Phantom Weapon objectives, Bozja Field Record, Occult Record and Variant Dungeon records."),
                    ["adventure", "contentsnote", "contentsnotecategory", "weeklybingotext", "notebookdivisioncategory", "relicnotecategory", "phantomweaponextododetailtxt", "mycwarresultnotebook", "mkdlore", "vvdnotebookcontents", "vvdnotebookseries"]),
                new TranslationPart(
                    "lore",
                    Loc.Localize("Part.Lore.Name", "Codex"),
                    Loc.Localize("Part.Lore.Tooltip", "Unending Codex entries."),
                    ["akatsukinotestring"],
                    Image: "codex")
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
