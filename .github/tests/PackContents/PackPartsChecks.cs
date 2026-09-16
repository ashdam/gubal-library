using GubalLibrary;

internal static class PackPartsChecks
{
    public static void Run()
    {
        var parts = PackParts.Groups.SelectMany(group => group.Parts).ToArray();
        var sheets = parts.SelectMany(part => part.Sheets).ToArray();
        Check(parts.All(part => part.Sheets.Length > 0), "Every part must contain sheets");
        Check(sheets.Distinct(StringComparer.OrdinalIgnoreCase).Count() == sheets.Length,
            "A sheet must belong to exactly one part");

        var menus = parts.Single(part => part.Sheets.Contains("addon"));
        string[] menuSheets =
        [
            "maincommand", "lobby", "custom/", "customtalk", "topicselect", "specialshop", "shop/",
            "transport/", "warp/", "warp", "raid/", "content/", "guild_order/", "leve/", "system/",
            "retainertaskrandom", "guildleveassignmenttalk",
        ];
        Check(menuSheets.All(menus.Sheets.Contains), "NPC services must use the general interface switch");
        Check(menus.Warning is not null, "Interface menus need a plugin compatibility warning");

        string[] gameplaySheets =
        [
            "quest/", "quest", "cut_scene/", "defaulttalk", "instancecontenttextdata", "fateevent",
            "gimmickbill", "action", "actiontransient", "trait", "traittransient", "status", "item",
            "goldsaucertextdata", "mkdsupportjob", "mkdtrait", "deepdungeonitem", "bankacraftworks",
            "guidepagestring", "xbmpet", "xbmitem", "bnpcname", "eobjname",
        ];
        Check(gameplaySheets.All(PackParts.IsKnown), "Gameplay sheets must have a named part");
        Check(!gameplaySheets.Any(menus.Sheets.Contains), "Gameplay sheets must stay outside interface menus");
        Check(parts.Single(part => part.Sheets.Contains("deepdungeonitem")) !=
              parts.Single(part => part.Sheets.Contains("mkdsupportjob")),
            "Deep Dungeons and field operation jobs need separate switches");

        Check(PackParts.SheetOf("exd/Quest_0_en.exd") == "quest", "Quest titles use the flat sheet");
        Check(PackParts.SheetOf("exd/quest/001/Test_0_en.exd") == "quest/", "Quest dialogue uses the family");
        Check(PackParts.SheetOf("exd/cut_scene/001/Test_0_en.exd") == "cut_scene/", "Cutscenes retain their family");
        Check(PackParts.SheetOf("EXD/Custom/001/Test_0_en.exd") == "custom/", "Family matching ignores case");
        Check(!PackParts.IsKnown("unlisted"), "New sheets must retain the fallback group");

        PackParts.Invalidate();
        Check(PackParts.Groups.SelectMany(group => group.Parts).SelectMany(part => part.Sheets)
                .SequenceEqual(sheets), "A language change must preserve sheet assignments");
        Console.WriteLine("Translation part checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
