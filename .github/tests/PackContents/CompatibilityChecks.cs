using GubalLibrary;
using System.Text.Json;

internal static class CompatibilityChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gubal-compatibility-" + Guid.NewGuid().ToString("N"));
        string[] excluded =
        [
            "exd/Addon_0_en.exd",
            "exd/addon_100_fr.exd",
            "exd/lobby_0_en.exd",
            "exd/eobjname_2000000_en.exd",
            "exd/warp_131072_en.exd",
            "EXD/Transport/Aetheryte_0_en.exd",
            "exd/transport/aetheryteishgard_0_en.exd",
            "exd/custom/001/CmnDefHousingPersonalRoomEntrance_00178_0_en.exd",
            "exd/custom/003/HouFixMansionEntrance_00359_0_en.exd",
            "exd/custom/007/CtsMjiEntrance_00798_0_en.exd",
        ];
        string[] kept =
        [
            "exd/transport/AetheryteTown_0_en.exd",
            "exd/transport/Other_0_en.exd",
            "exd/custom/001/CmnDefRetainerCall_00010_0_en.exd",
            "exd/custom/003/HouFixMansionExit_00362_0_en.exd",
            "exd/warp/WarpInnLimsaLominsa_0_en.exd",
            "exd/placename_0_en.exd",
            "exd/aetheryte_0_en.exd",
            "exd/bnpcname_0_en.exd",
            "exd/item_0_en.exd",
            "exd/quest/001/Test_0_en.exd",
        ];

        try
        {
            foreach (var relative in excluded.Concat(kept).Concat(new[]
                     { "ui/uld/test.uld", "ui/icon/120000/en/120021.tex", "common/font/axis_12.fdt" }))
            {
                var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, []);
            }

            var pack = PackContents.Load(root, 1000);
            var disabled = new HashSet<string> { "item" };
            var baseline = pack.Servable(disabled);
            var compatible = pack.Servable(disabled, true);
            Check(excluded.All(path => !compatible.ContainsKey(path)), "Exclude all Lifestream sheets");
            Check(kept.Where(path => !path.Contains("item_")).All(compatible.ContainsKey), "Keep unrelated sheets in the same families");
            Check(!compatible.ContainsKey("exd/item_0_en.exd"), "Keep manual exclusions");
            Check(disabled.SetEquals(["item"]), "Compatibility must not change manual selections");
            Check(pack.Servable(disabled, false).Keys.SequenceEqual(baseline.Keys), "Turning compatibility off restores manual selections");
            Check(pack.Servable([], true).Count == kept.Length, "Turning all parts on does not bypass compatibility");
            Check(pack.Servable(["custom/"], true).Keys.All(path => !path.Contains("/custom/", StringComparison.OrdinalIgnoreCase)), "Manual family exclusions still apply");
            Check(pack.ServableLayouts([], true).Count == 0 && pack.ServableScreenImages([], true).Count == 0,
                "Addon layouts and images follow the original text");
            Check(pack.ServableLayouts([]).Count == 1 && pack.ServableScreenImages([]).Count == 1,
                "Turning compatibility off restores Addon assets");
            Check(pack.FontCount == 1, "Fonts remain available");

            var settings = new Configuration { LifestreamCompatibility = true, DisabledSheets = ["item", "custom/"] };
            var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(settings))!;
            Check(restored.LifestreamCompatibility && restored.DisabledSheets.SetEquals(settings.DisabledSheets),
                "Save both compatibility and manual exclusions");
            Check(pack.Servable(restored.DisabledSheets, restored.LifestreamCompatibility).Keys
                .SequenceEqual(pack.Servable(settings.DisabledSheets, true).Keys), "Restore the same page selection");
            restored.LifestreamCompatibility = false;
            Check(pack.Servable(restored.DisabledSheets, false).Keys.SequenceEqual(pack.Servable(settings.DisabledSheets).Keys),
                "Disabling compatibility must preserve saved manual exclusions");
            restored.LifestreamCompatibility = true;
            restored.DisabledSheets.Clear();
            Check(restored.LifestreamCompatibility && pack.Servable(restored.DisabledSheets, restored.LifestreamCompatibility).Count == kept.Length,
                "The all-parts reset must preserve compatibility");
            restored.DisabledSheets.UnionWith(pack.Layout.SelectMany(group => group.Parts).SelectMany(part => part.Sheets));
            Check(pack.Servable(restored.DisabledSheets, restored.LifestreamCompatibility).Count == 0,
                "Compatibility must not enable any manually disabled page");
            var legacy = JsonSerializer.Deserialize<Configuration>("""{"DisabledSheets":["addon"]}""")!;
            Check(!legacy.LifestreamCompatibility && legacy.DisabledSheets.Contains("addon"), "Old settings retain their selections and leave compatibility off");
            Console.WriteLine("Compatibility checks passed.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
