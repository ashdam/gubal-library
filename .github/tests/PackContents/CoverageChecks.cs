using System.Text.Json;
using GubalLibrary;

internal static class CoverageChecks
{
    public static void Run()
    {
        var manifest = JsonSerializer.Deserialize<PackManifest>("""
            {
              "gameVersion": "2026.09.01.0000.0000",
              "coverage": {
                "groups": [
                  { "expansion": "Heavensward", "translated": 10, "total": 10 },
                  { "expansion": "Heavensward", "translated": 0, "total": 90 },
                  { "expansion": "Dawntrail", "translated": 0, "total": 0 },
                  { "expansion": null, "translated": 15, "total": 30 }
                ],
                "excludedFromLocalization": [
                  { "expansion": "Heavensward", "translated": 0, "total": 900 }
                ]
              }
            }
            """)!;
        var expansions = manifest.Coverage!.ByExpansion.ToDictionary(e => e.Name);
        Check(manifest.GameVersion == "2026.09.01.0000.0000", "Keep the full game version");
        Check(expansions["Heavensward"].Percent == 10, "Weight coverage by text count and omit excluded text");
        Check(expansions["Dawntrail"].Percent == 0, "Empty expansion does not divide by zero");
        Check(expansions[""].Percent == 50, "Shared text stays separate from expansions");
        Check(new PackCoverage().ByExpansion.Count() == 0, "Old manifests have no expansion breakdown");
        Check(new ExpansionCoverage("test", 1, 3).Percent is > 33 and < 34, "Keep fractional coverage");
        Console.WriteLine("Coverage checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
