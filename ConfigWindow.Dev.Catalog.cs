using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private nint nativeImageModes;
    private nint nativeImageIndex;
    private nint nativeCompanyImages;
    private readonly Dictionary<int, PreviewScene> gameBannerScenes = [];

    private void ConfigureSceneCatalog(ISigScanner scanner)
    {
        if (scanner.TryScanText("BA 02 00 00 00 41 83 FA 30 73 09 41 0F BE 94 0A ?? ?? ?? ?? 8B 84 24", out var modes))
            this.nativeImageModes = scanner.Module.BaseAddress + Marshal.ReadInt32(modes + 16);
        scanner.TryScanText("83 FA 4D 0F 87 ?? ?? ?? ?? 8B C2 48 8D 15 ?? ?? ?? ?? 8B 8C 82", out this.nativeImageIndex);
        if (scanner.TryScanText("4C 8D 25 ?? ?? ?? ?? 42 8B 1C 27 48 8D 4D 80", out var companies))
            this.nativeCompanyImages = companies + 7 + Marshal.ReadInt32(companies + 3);
    }

    private unsafe int NativeSceneIndex(int mode)
    {
        if (this.nativeImageIndex == 0 || mode is < 0 or > 77) return -1;
        var lookup = (delegate* unmanaged<nint, int, int>)this.nativeImageIndex;
        var index = lookup(0, mode);
        return index is >= 0 and < 69 ? index : -1;
    }

    private int NativeImageMode(int type)
    {
        if (this.nativeImageModes == 0 || type is < 1 or >= 48) return -1;
        // These types use the native short location title dispatcher.
        if (type is 16 or 23 or 33) return 6;
        var mode = Marshal.ReadByte(this.nativeImageModes + type);
        return this.NativeSceneIndex(mode) >= 0 ? mode : -1;
    }

    private void ReadGameBannerScenes()
    {
        this.gameBannerScenes.Clear();
        if (this.previewData is null) return;
        foreach (var screen in this.previewData.GetExcelSheet<ScreenImage>()!.OrderBy(row => row.RowId))
            if (screen.Lang && this.NativeImageMode(screen.Type) >= 0)
                this.gameBannerScenes.TryAdd((int)screen.Image, new(ScreenImageId: screen.RowId));
        foreach (var cut in this.previewData.GetExcelSheet<CutScreenImage>()!.OrderBy(row => row.RowId))
        {
            if (this.NativeImageMode(cut.Type) < 0) continue;
            var scene = new PreviewScene(CutScreenImageId: cut.RowId);
            foreach (var image in this.SceneImages(scene)) this.gameBannerScenes.TryAdd(image, scene);
        }
        foreach (var territory in this.previewData.GetExcelSheet<TerritoryType>()!.OrderBy(row => row.RowId))
        {
            if (territory.PlaceNameIcon > 0)
                this.gameBannerScenes.TryAdd(territory.PlaceNameIcon, new(TerritoryId: territory.RowId));
            if (territory.PlaceNameRegionIcon > 0)
                this.gameBannerScenes.TryAdd(territory.PlaceNameRegionIcon, new(TerritoryId: territory.RowId));
        }
        if (this.nativeCompanyImages != 0)
            for (var company = 1; company <= 3; company++)
            {
                var image = Marshal.ReadInt32(this.nativeCompanyImages + company * 8 + 4);
                if (image > 0) this.gameBannerScenes.TryAdd(image, new(CompanyId: (uint)company));
            }
    }

    private int SceneMode(PreviewScene scene)
    {
        if (scene.CutScreenImageId != 0)
            return this.NativeImageMode(this.previewData!.GetExcelSheet<CutScreenImage>()!.GetRow(scene.CutScreenImageId).Type);
        if (scene.ScreenImageId != 0)
            return this.NativeImageMode(this.previewData!.GetExcelSheet<ScreenImage>()!.GetRow(scene.ScreenImageId).Type);
        if (scene.Streak != 0) return 30;
        if (scene.ManeuverStreak != 0) return 31;
        if (scene.TerritoryId != 0) return 10;
        if (scene.CompanyId != 0) return (int)this.previewData!.Language is 0 or >= 4 ? 19 : 18;
        if (scene.ImageId != 0) return scene.DisplayType switch { 0 => 2, 1 => 9, 2 => 16, 3 => 29, _ => -1 };
        return -1;
    }
}
