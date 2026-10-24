using System.Text.Json;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private Dictionary<int, PreviewScene> ReadNativeScenes()
    {
        var path = Path.Combine(this.previewCatalogPath!, "dev", "banner-native-scenes.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (doc.RootElement.GetProperty("version").GetInt32() != 2)
            throw new InvalidDataException("Unsupported native banner catalog version.");
        if (doc.RootElement.GetProperty("gameVersion").GetString() != this.RunningGame())
            throw new InvalidDataException("The native banner catalog does not match the running game. Rebuild the catalog and pack.");
        if (this.previewData?.Language != Dalamud.Game.ClientLanguage.English)
            throw new InvalidDataException("Native banner previews require the English game client.");
        var result = new Dictionary<int, PreviewScene>();
        foreach (var row in doc.RootElement.GetProperty("scenes").EnumerateArray())
        {
            var id = row.GetProperty("textureId").GetInt32();
            var mode = row.GetProperty("mode").GetInt32();
            var index = row.GetProperty("index").GetInt32();
            var images = row.GetProperty("images").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var requests = row.GetProperty("requestImages").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            uint Required(string name)
            {
                var value = row.GetProperty(name).GetUInt32();
                return value > 0 ? value : throw new InvalidDataException($"Invalid {name} for native banner {id}.");
            }
            var scene = row.GetProperty("type").GetString() switch
            {
                "screenImage" => new PreviewScene(ScreenImageId: Required("rowId")),
                "cutScreenImage" => new PreviewScene(CutScreenImageId: Required("rowId")),
                "streak" when Required("value") is 1 or 2 => new PreviewScene(Streak: (int)Required("value")),
                "maneuverStreak" when Required("value") == 1 => new PreviewScene(ManeuverStreak: 1),
                "companyRank" when Required("companyId") is >= 1 and <= 3 => new PreviewScene(CompanyId: Required("companyId")),
                "directImage" when row.GetProperty("displayType").GetInt32() is >= 0 and <= 3 =>
                    new PreviewScene(ImageId: (uint)id, DisplayType: row.GetProperty("displayType").GetInt32()),
                "locationTitle" => new PreviewScene(TerritoryId: Required("territoryId"),
                    LocationImageId: row.TryGetProperty("replacementTitleId", out _) ? Required("replacementTitleId") : 0,
                    SourceTitleId: row.TryGetProperty("sourceTitleId", out _) ? Required("sourceTitleId") : 0),
                _ => throw new InvalidDataException($"Unsupported native banner type for {id}."),
            };
            if (id is < 120000 or >= 130000 || mode is < 0 or > 77 || index is < 0 or >= 69
                || images.Length is < 1 or > 8 || requests.Length is < 1 or > 8 || !images.Contains(id)
                || images.Concat(requests).Any(image => image is < 120000 or >= 130000)
                || !result.TryAdd(id, scene with { Mode = mode, Index = index, Images = images, RequestImages = requests }))
                throw new InvalidDataException($"Invalid or duplicate native banner configuration: {id}.");
        }
        return result;
    }
}
