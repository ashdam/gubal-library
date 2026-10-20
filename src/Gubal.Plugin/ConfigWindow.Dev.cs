using System.Text.Json;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using TextureResourceHandle = FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.TextureResourceHandle;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    public bool PreviewPluginUpdate { get; set; }
    private readonly bool isDev;
    private readonly IDataManager? previewData;
    private readonly IGameGui? previewGui;
    private int previewId = 120052;
    private bool previewOriginal;
    private readonly Func<bool>? canPreview;
    private bool previewRequested;
    private string previewFilter = string.Empty;
    private string? previewCatalogPath;
    private string? previewCatalogError;
    private BannerPreview[] previewBanners = [];
    private Task<BannerPreview[]>? previewCatalogLoad;
    private CancellationTokenSource? previewCatalogCancellation;
    private CancellationTokenSource? previewCancellation;
    private bool previewNeedsShow;
    private IAddonLifecycle? previewLifecycle;
    private IFramework? previewFramework;

    private sealed record BannerPreview(int Id, string Name,
        string RelativePath = "", string OriginalName = "", PreviewScene? NativeScene = null, bool HasTranslation = false);

    private sealed record PreviewScene(uint CutScreenImageId = 0, uint ScreenImageId = 0, int Streak = 0, int ManeuverStreak = 0, uint TerritoryId = 0, uint CompanyId = 0, uint ImageId = 0, int DisplayType = 0, uint LocationImageId = 0);
    private sealed record SceneTexture(nint Asset, AtkTexture Texture, nint Metadata);
    private Task<Dictionary<int, IDalamudTextureWrap>>? previewSceneTextures;
    private readonly Dictionary<int, nint> nativeSceneTextures = [];
    private readonly Dictionary<nint, List<SceneTexture>> savedSceneTextures = [];
    private PreviewScene? previewScene;
    private nint showScreenImage;
    private nint showCutScreenImage;
    private nint showRawImage;
    private readonly HashSet<int> nativeSceneSeen = [];
    private int[] requiredNativeImages = [];

    public void ConfigureDevPreview(IAddonLifecycle lifecycle, ISigScanner scanner, IFramework framework)
    {
        if (!this.isDev) return;
        this.previewLifecycle = lifecycle;
        this.previewFramework = framework;
        this.ConfigureSceneControl(scanner);
        this.ConfigureSceneCatalog(scanner);
        scanner.TryScanText("48 83 EC 38 44 8B D2 BA 02 00 00 00 41 83 F9 03", out this.showRawImage);
        scanner.TryScanText("48 89 5C 24 08 57 48 83 EC 40 48 8B F9 8B DA 8B CA E8 ?? ?? ?? ?? 4C 8B C8 48 85 C0 75 0B 48 8B 5C 24 50 48 83 C4 40 5F C3 F6 05", out this.showScreenImage);
        scanner.TryScanText("48 89 5C 24 08 57 48 83 EC 40 48 8B F9 8B DA 8B CA E8 ?? ?? ?? ?? 4C 8B C8 48 85 C0 75 0B 48 8B 5C 24 50 48 83 C4 40 5F C3 0F BF 48 0A", out this.showCutScreenImage);
        lifecycle.RegisterListener(AddonEvent.PreDraw, this.PrepareDevPreview);
        lifecycle.RegisterListener(AddonEvent.PostDraw, this.RestoreDevPreview);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, this.RestoreDevPreview);
    }

    public void DisposeDevPreview()
    {
        this.previewCatalogCancellation?.Cancel();
        this.previewCatalogCancellation?.Dispose();
        this.previewCatalogCancellation = null;
        this.previewLifecycle?.UnregisterListener(this.PrepareDevPreview, this.RestoreDevPreview);
        this.previewLifecycle = null;
        if (this.previewFramework is { IsInFrameworkUpdateThread: false } framework)
            _ = framework.RunOnFrameworkThread(this.ReleaseDevTexture);
        else this.ReleaseDevTexture();
        this.previewFramework = null;
    }

    private unsafe void ReleaseDevTexture()
    {
        foreach (var addon in this.savedSceneTextures.Keys.ToArray()) this.RestoreSceneTextures(addon);
        this.CancelSceneRequest();
        foreach (var texture in this.nativeSceneTextures.Values) ((KernelTexture*)texture)->DecRef();
        this.nativeSceneTextures.Clear();
        this.nativeSceneSeen.Clear();
        this.requiredNativeImages = [];
        if (this.previewSceneTextures is { } sceneTask)
            _ = sceneTask.ContinueWith(done =>
            {
                if (done.Status == TaskStatus.RanToCompletion)
                    foreach (var texture in done.Result.Values) texture.Dispose();
                else _ = done.Exception;
            }, TaskScheduler.Default);
        this.previewSceneTextures = null;
        this.previewScene = null;
        this.previewCancellation?.Cancel();
        this.previewCancellation?.Dispose();
        this.previewCancellation = null;
        this.previewNeedsShow = false;
    }

    private void LoadDevBanners()
    {
        if (!this.isDev) return;
        this.ReleaseDevTexture();
        this.previewCatalogPath = this.config.LanguagePackPath;
        this.previewCatalogError = null;
        this.previewBanners = [];
        this.previewCatalogCancellation?.Cancel();
        this.previewCatalogCancellation?.Dispose();
        this.previewCatalogCancellation = new CancellationTokenSource();
        var token = this.previewCatalogCancellation.Token;
        try
        {
            var names = new Dictionary<int, BannerPreview>();
            var catalog = Path.Combine(this.previewCatalogPath, "dev", "banner-previews.json");
            if (File.Exists(catalog))
            {
                try
                {
                    using var stream = File.OpenRead(catalog);
                    foreach (var row in JsonSerializer.Deserialize<BannerPreview?[]>(stream) ?? [])
                        if (row is not null) names.TryAdd(row.Id, row);
                }
                catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
                {
                    this.previewCatalogError = error.Message;
                }
            }
            this.ReadGameBannerScenes();
            var scenes = new Dictionary<int, PreviewScene>(this.gameBannerScenes);
            var pack = this.previewCatalogPath;
            this.previewCatalogLoad = Task.Run(() =>
            {
                var rows = new List<BannerPreview>();
                for (var id = 120000; id < 130000; id++)
                {
                    token.ThrowIfCancellationRequested();
                    var relative = $"ui/icon/{id / 1000 * 1000:D6}/en/{id}.tex";
                    if (this.previewData?.FileExists(relative) != true) continue;
                    var translated = File.Exists(Path.Combine(pack, relative));
                    var label = names.GetValueOrDefault(id);
                    var scene = label?.NativeScene ?? scenes.GetValueOrDefault(id);
                    rows.Add(new(id, translated ? label?.Name ?? "Texto sin catalogar" : "N/A",
                        RelativePath: relative, OriginalName: label?.OriginalName ?? "",
                        NativeScene: scene, HasTranslation: translated));
                }
                return rows.OrderBy(row => row.Id).ToArray();
            }, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            this.previewCatalogError = error.Message;
        }
    }

    private void ShowDevBanner()
    {
        if (!this.isDev || this.previewData is null || this.canPreview?.Invoke() != true) return;
        this.ReleaseDevTexture();
        var row = this.previewBanners.FirstOrDefault(row => row.Id == this.previewId);
        if (row is null)
        {
            this.previewCatalogError = $"No se ha encontrado el rótulo {this.previewId} en el juego.";
            return;
        }
        if (!this.previewOriginal && !row.HasTranslation)
        {
            this.previewCatalogError = $"N/A: {row.Id} no tiene traducción en el LP activo.";
            return;
        }
        if (row.NativeScene is not { } scene)
        {
            this.previewCatalogError = $"La presentación nativa de {row.Id} todavía no está identificada.";
            return;
        }
        this.previewCatalogError = null;
        this.previewCancellation = new CancellationTokenSource();
        this.previewScene = scene;
        this.previewSceneTextures = this.LoadSceneTextures(scene, this.previewOriginal, this.previewCancellation.Token);
        this.previewNeedsShow = true;
    }

    private async Task<Dictionary<int, IDalamudTextureWrap>> LoadSceneTextures(
        PreviewScene scene, bool original, CancellationToken token)
    {
        var result = new Dictionary<int, IDalamudTextureWrap>();
        try
        {
            foreach (var id in this.SceneImages(scene))
            foreach (var scale in new[] { 1, 2 })
            {
                var relative = $"ui/icon/{id / 1000 * 1000:D6}/en/{id}{(scale == 2 ? "_hr1" : "")}.tex";
                var native = await Task.Run(() => this.previewData!.GetFile<TexFile>(relative), token)
                    ?? throw new FileNotFoundException("Native scene texture not found.", relative);
                var translated = Path.Combine(this.config.LanguagePackPath, relative);
                if (!original && File.Exists(translated))
                {
                    var data = await File.ReadAllBytesAsync(translated, token);
                    if (data.Length < 80 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)) != native.Header.Width
                        || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)) != native.Header.Height)
                        throw new InvalidDataException("Scene texture dimensions differ from the game.");
                    result.Add(id * 10 + scale, await this.textures.CreateFromImageAsync(data, "Gubal native scene: " + relative, token));
                }
                else result.Add(id * 10 + scale, await this.textures.CreateFromTexFileAsync(native, "Gubal native scene: " + relative, token));
            }
            return result;
        }
        catch
        {
            foreach (var texture in result.Values) texture.Dispose();
            throw;
        }
    }

    private unsafe void UpdateScenePreview()
    {
        if (this.previewSceneTextures is not { IsCompleted: true } task) return;
        this.previewNeedsShow = false;
        if (!task.IsCompletedSuccessfully)
        {
            this.previewCatalogError = task.Exception?.GetBaseException().Message ?? "Native scene loading was cancelled.";
            return;
        }
        try
        {
            var ui = UIModule.Instance();
            if (ui == null || this.canPreview?.Invoke() != true) return;
            this.requiredNativeImages = [this.previewId];
            foreach (var (id, texture) in task.Result)
            {
                var kernel = this.textures.ConvertToKernelTexture(texture, leaveWrapOpen: true);
                if (kernel == 0) throw new InvalidOperationException("Native scene texture creation failed.");
                this.nativeSceneTextures.Add(id, kernel);
            }
            var scene = this.previewScene!;
            var mode = this.SceneMode(scene);
            var index = this.NativeSceneIndex(mode);
            if (index < 0) throw new InvalidOperationException("Native scene control is unavailable.");
            var requestImages = scene.CutScreenImageId != 0
                ? new[] { this.previewData!.GetExcelSheet<Lumina.Excel.Sheets.CutScreenImage>()!.GetRow(scene.CutScreenImageId).Image }
                : scene.TerritoryId != 0 ? this.TerritoryImages(scene, true) : this.SceneImages(scene);
            this.BeginSceneRequest(mode, index, requestImages);
            if (scene.Streak != 0) ui->ShowStreak(scene.Streak, 0);
            else if (scene.ManeuverStreak != 0) ui->ShowAddonKillStreakForManeuvers(scene.ManeuverStreak, 0);
            else if (scene.CompanyId != 0) ui->ShowGrandCompanyRankUp(scene.CompanyId, 1, false);
            else if (scene.ImageId != 0)
            {
                if (this.showRawImage == 0) throw new InvalidOperationException("Native image function was not found.");
                var show = (delegate* unmanaged<RaptureAtkModule*, uint, byte, int, int, byte>)this.showRawImage;
                if (show(ui->GetRaptureAtkModule(), scene.ImageId, 1, scene.DisplayType, -1) == 0)
                    throw new InvalidOperationException("The game did not open the native image.");
            }
            else if (scene.TerritoryId != 0)
            {
                var language = 1;
                ui->ShowLocationTitle(scene.TerritoryId, true, true, &language);
            }
            else
            {
                var function = scene.CutScreenImageId != 0 ? this.showCutScreenImage : this.showScreenImage;
                if (function == 0) throw new InvalidOperationException("Native screen image function was not found.");
                var show = (delegate* unmanaged<RaptureAtkModule*, uint, byte>)function;
                var rowId = scene.CutScreenImageId != 0 ? scene.CutScreenImageId : scene.ScreenImageId;
                if (show(ui->GetRaptureAtkModule(), rowId) == 0)
                    throw new InvalidOperationException("The game did not open the native scene.");
            }
            this.CaptureSceneChildren();
        }
        catch (Exception error)
        {
            this.previewCatalogError = error.Message;
        }
    }

    private int[] SceneImages(PreviewScene scene)
    {
        if (scene.ImageId != 0) return [(int)scene.ImageId];
        if (scene.CutScreenImageId != 0)
        {
            var row = this.previewData!.GetExcelSheet<Lumina.Excel.Sheets.CutScreenImage>()!.GetRow(scene.CutScreenImageId);
            return row.Type switch
            {
                5 => [row.Image + 1, row.Image + 2, row.Image + 3],
                4 => [row.Image, row.Image + 1],
                8 => [row.Image, row.Image + 10, row.Image + 20],
                16 => [122018, row.Image],
                23 => [122010, row.Image],
                33 => [122002, row.Image],
                _ => [row.Image],
            };
        }
        if (scene.ScreenImageId != 0)
            return [(int)this.previewData!.GetExcelSheet<Lumina.Excel.Sheets.ScreenImage>()!.GetRow(scene.ScreenImageId).Image];
        if (scene.TerritoryId != 0)
            return this.TerritoryImages(scene);
        if (scene.CompanyId != 0 && this.nativeCompanyImages != 0)
            return [Marshal.ReadInt32(this.nativeCompanyImages + (int)scene.CompanyId * 8 + 4)];
        if (scene.Streak is 1 or 2) return [120950 + scene.Streak];
        if (scene.ManeuverStreak == 1) return [120955];
        throw new InvalidDataException("Unsupported native scene.");
    }

    private int[] TerritoryImages(PreviewScene scene, bool nativeTitle = false)
    {
        var row = this.previewData!.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!.GetRow(scene.TerritoryId);
        var title = !nativeTitle && scene.LocationImageId != 0 ? (int)scene.LocationImageId : row.PlaceNameIcon;
        return new[] { row.PlaceNameRegionIcon, title }.Where(id => id > 0).Distinct().ToArray();
    }

    private int SceneTextureId(int id)
    {
        if (this.previewScene is not { TerritoryId: > 0, LocationImageId: > 0 } scene) return id;
        var territory = this.previewData!.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!.GetRow(scene.TerritoryId);
        return id == territory.PlaceNameIcon ? (int)scene.LocationImageId : id;
    }

    private unsafe bool TrySceneTexture(int id, KernelTexture* native, out nint texture)
    {
        id = this.SceneTextureId(id);
        foreach (var scale in new[] { 1, 2 })
        {
            if (!this.nativeSceneTextures.TryGetValue(id * 10 + scale, out var candidate)) continue;
            var kernel = (KernelTexture*)candidate;
            if (kernel->ActualWidth != native->ActualWidth || kernel->ActualHeight != native->ActualHeight) continue;
            texture = candidate;
            return true;
        }
        texture = 0;
        return false;
    }

    private unsafe void PrepareSceneTextures(AtkUnitBase* addon)
    {
        if (addon == null || this.nativeSceneTextures.Count == 0 || !this.IsSceneChild(addon)) return;
        var saved = new List<SceneTexture>();
        try
        {
            var images = new List<nint>();
            CollectSceneImages(&addon->UldManager, images);
            foreach (var address in images)
            {
                var image = (AtkImageNode*)address;
                if (image->PartsList == null || image->PartId >= image->PartsList->PartCount) continue;
                var asset = image->PartsList->Parts[image->PartId].UldAsset;
                if (asset == null || asset->AtkTexture.TextureType != TextureType.Resource || asset->AtkTexture.Resource == null) continue;
                var resource = asset->AtkTexture.Resource;
                if (resource->TexFileResourceHandle == null || saved.Any(entry => entry.Asset == (nint)asset)) continue;
                var native = resource->TexFileResourceHandle->Texture;
                if (native == null) continue;
                var id = (int)resource->IconId;
                if (!this.TrySceneTexture(id, native, out var texture))
                {
                    var name = Path.GetFileNameWithoutExtension(resource->TexFileResourceHandle->FileName.ToString());
                    if (name.EndsWith("_hr1", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    if (!int.TryParse(name, out id) || !this.TrySceneTexture(id, native, out texture)) continue;
                }
                var memory = NativeMemory.Alloc((nuint)(sizeof(AtkTextureResource) + sizeof(TextureResourceHandle)));
                if (memory == null) throw new OutOfMemoryException();
                var copy = (AtkTextureResource*)memory;
                *copy = *resource;
                var handle = (TextureResourceHandle*)(copy + 1);
                *handle = *resource->TexFileResourceHandle;
                handle->Texture = (KernelTexture*)texture;
                copy->TexFileResourceHandle = handle;
                copy->KernelTextureObject = (KernelTexture*)texture;
                // Borrow metadata during drawing. Do not register or release these copies with the game.
                saved.Add(new SceneTexture((nint)asset, asset->AtkTexture, (nint)memory));
                asset->AtkTexture.Resource = copy;
                this.nativeSceneSeen.Add(this.SceneTextureId(id));
                this.RememberSceneChild(addon);
            }
        }
        catch (Exception error)
        {
            this.previewCatalogError = error.Message;
        }
        if (saved.Count > 0) this.savedSceneTextures[(nint)addon] = saved;
    }

    private static unsafe void CollectSceneImages(AtkUldManager* manager, List<nint> images, int depth = 0)
    {
        if (manager == null || depth > 16) return;
        for (var index = 0; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null) continue;
            if (node->Type == NodeType.Image) images.Add((nint)node);
            else if ((int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) CollectSceneImages(&component->UldManager, images, depth + 1);
            }
        }
    }

    private unsafe void RestoreSceneTextures(nint addon)
    {
        if (!this.savedSceneTextures.Remove(addon, out var saved)) return;
        foreach (var entry in saved)
        {
            ((AtkUldAsset*)entry.Asset)->AtkTexture = entry.Texture;
            NativeMemory.Free((void*)entry.Metadata);
        }
    }

    private void UpdateDevPreview()
    {
        if (this.previewCatalogLoad is { IsCompleted: true } catalog)
        {
            this.previewCatalogLoad = null;
            if (catalog.IsCompletedSuccessfully) this.previewBanners = catalog.Result;
            else if (catalog.IsFaulted) this.previewCatalogError = catalog.Exception!.GetBaseException().Message;
        }
        if (this.previewNeedsShow && this.previewScene is not null) this.UpdateScenePreview();
    }

    private unsafe void PrepareDevPreview(AddonEvent _, AddonArgs args)
    {
        if (this.previewScene is not null) this.PrepareSceneTextures((AtkUnitBase*)args.Addon.Address);
    }

    private void RestoreDevPreview(AddonEvent _, AddonArgs args)
        => this.RestoreSceneTextures(args.Addon.Address);

    private void DrawDevTab()
    {
        if (!this.isDev) return;
        using var tab = ImRaii.TabItem("Dev");
        if (!tab) return;
        var previewUpdate = this.PreviewPluginUpdate;
        if (ImGui.Checkbox(CheapLoc.Loc.Localize("PluginUpdate.PreviewToggle", "Preview lobby update notice"), ref previewUpdate))
            this.PreviewPluginUpdate = previewUpdate;
        if (this.previewCatalogPath != this.config.LanguagePackPath) this.LoadDevBanners();
        if (this.previewRequested)
        {
            this.previewRequested = false;
            this.ShowDevBanner();
        }
        this.UpdateDevPreview();
        ImGui.TextWrapped("Original leído del juego y traducción leída del LP activo.");
        var selected = this.previewBanners.FirstOrDefault(row => row.Id == this.previewId);
        ImGui.TextWrapped(selected?.NativeScene is not null
            ? "Escena nativa: el juego controla el tamaño, la posición y la animación."
            : "Presentación nativa pendiente de identificar. No se mostrará una composición aproximada.");
        if (this.previewScene is not null)
            ImGui.TextWrapped(this.requiredNativeImages.Length > 0 && this.requiredNativeImages.All(this.nativeSceneSeen.Contains)
                ? "Texturas de comparación verificadas en los nodos nativos."
                : "Esperando a que el juego dibuje las texturas de la escena.");
        if (ImGui.Button("Recargar lista")) this.LoadDevBanners();
        ImGui.SameLine();
        ImGui.TextUnformatted($"{this.previewBanners.Length} rótulos");
        if (this.previewCatalogLoad is { IsCompleted: false }) ImGui.TextUnformatted("Cargando catálogo del juego…");
        ImGui.InputText("Buscar", ref this.previewFilter, 200);
        ImGui.InputInt("ID de textura", ref this.previewId);
        if (ImGui.Button("Mostrar de nuevo")) this.previewRequested = true;
        ImGui.SameLine();
        if (ImGui.Button("Detener prueba")) this.ReleaseDevTexture();
        if (this.previewCatalogError != null) ImGui.TextWrapped(this.previewCatalogError);
        if (this.previewNeedsShow) ImGui.TextUnformatted("Cargando textura…");
        using var list = ImRaii.Child("##devBanners");
        if (!list) return;
        using var table = ImRaii.Table("##devBannerComparison", 3,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg);
        if (!table) return;
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Original");
        ImGui.TableSetupColumn("Localización");
        ImGui.TableHeadersRow();
        foreach (var row in this.previewBanners)
        {
            var label = $"{row.Id} {row.OriginalName} {row.Name}";
            if (this.previewFilter.Length > 0 && CultureInfo.InvariantCulture.CompareInfo.IndexOf(label,
                    this.previewFilter, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) < 0) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Id.ToString());
            ImGui.TableNextColumn();
            var original = string.IsNullOrWhiteSpace(row.OriginalName) ? "Texto sin catalogar" : row.OriginalName;
            if (ImGui.RadioButton($"{original}##original{row.Id}", this.previewId == row.Id && this.previewOriginal))
            {
                this.previewId = row.Id;
                this.previewOriginal = true;
                this.previewRequested = true;
            }
            ImGui.TableNextColumn();
            if (!row.HasTranslation)
            {
                ImGui.TextUnformatted("N/A");
                continue;
            }
            if (ImGui.RadioButton($"{row.Name}##translated{row.Id}", this.previewId == row.Id && !this.previewOriginal))
            {
                this.previewId = row.Id;
                this.previewOriginal = false;
                this.previewRequested = true;
            }
        }
    }
}
