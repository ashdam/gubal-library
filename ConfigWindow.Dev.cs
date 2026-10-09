using System.Text.Json;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures.TextureWraps;
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
    private int loadedPreviewId;
    private string previewFilter = string.Empty;
    private string? previewCatalogPath;
    private string? previewCatalogError;
    private BannerPreview[] previewBanners = [];
    private CancellationTokenSource? previewCancellation;
    private Task<IDalamudTextureWrap>? previewTexture;
    private bool previewNeedsShow;
    private nint nativePreviewTexture;
    private int nativePreviewWidth;
    private int nativePreviewHeight;
    private IAddonLifecycle? previewLifecycle;
    private nint previewNode;
    private nint previewPart;
    private nint previewAsset;
    private AtkTexture savedPreviewTexture;
    private AtkUldPart savedPreviewPart;
    private ushort savedPreviewHeight;
    private float savedPreviewY;

    private sealed record BannerPreview(int Id, string Name, int OriginalId = 0,
        string RelativePath = "", string OriginalName = "");

    public void ConfigureDevPreview(IAddonLifecycle lifecycle)
    {
        if (!this.isDev) return;
        this.previewLifecycle = lifecycle;
        lifecycle.RegisterListener(AddonEvent.PreDraw, "_Image", this.PrepareDevPreview);
        lifecycle.RegisterListener(AddonEvent.PostDraw, "_Image", this.RestoreDevPreview);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "_Image", this.RestoreDevPreview);
    }

    public void DisposeDevPreview()
    {
        this.ReleaseDevTexture();
        this.previewLifecycle?.UnregisterListener(this.PrepareDevPreview, this.RestoreDevPreview);
        this.previewLifecycle = null;
    }

    private unsafe void ReleaseDevTexture()
    {
        if (this.previewNode != 0 && this.previewGui is not null)
            this.RestoreNativePreview((AddonImage*)this.previewGui.GetAddonByName("_Image").Address);
        this.previewCancellation?.Cancel();
        this.previewCancellation?.Dispose();
        this.previewCancellation = null;
        if (this.nativePreviewTexture != 0)
        {
            ((KernelTexture*)this.nativePreviewTexture)->DecRef();
            this.nativePreviewTexture = 0;
        }
        Release(this.previewTexture);
        this.previewTexture = null;
        this.previewNeedsShow = false;
        this.loadedPreviewId = 0;

        static void Release(Task<IDalamudTextureWrap>? task)
        {
            if (task is null) return;
            _ = task.ContinueWith(done =>
            {
                if (done.Status == TaskStatus.RanToCompletion) done.Result.Dispose();
                else _ = done.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void LoadDevBanners()
    {
        if (!this.isDev) return;
        this.ReleaseDevTexture();
        this.previewCatalogPath = this.config.LanguagePackPath;
        this.previewCatalogError = null;
        this.previewBanners = [];
        if (string.IsNullOrWhiteSpace(this.previewCatalogPath)) return;
        try
        {
            var names = new Dictionary<int, BannerPreview>();
            var aliases = new HashSet<int>();
            var catalog = Path.Combine(this.previewCatalogPath, "dev", "banner-previews.json");
            if (File.Exists(catalog))
            {
                try
                {
                    using var stream = File.OpenRead(catalog);
                    foreach (var row in JsonSerializer.Deserialize<BannerPreview?[]>(stream) ?? [])
                    {
                        if (row is null) continue;
                        names.TryAdd(row.Id, row);
                        if (row.OriginalId != 0) aliases.Add(row.OriginalId);
                    }
                }
                catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
                {
                    this.previewCatalogError = error.Message;
                }
            }
            var root = Path.Combine(this.previewCatalogPath, "ui", "icon");
            if (!Directory.Exists(root)) return;
            var rows = new List<BannerPreview>();
            foreach (var path in Directory.EnumerateFiles(root, "*.tex", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(this.previewCatalogPath, path).Replace(Path.DirectorySeparatorChar, '/');
                var parts = relative.Split('/');
                if (parts.Length != 5 || parts[3] != "en"
                    || !int.TryParse(Path.GetFileNameWithoutExtension(path), out var id)
                    || id is < 120000 or > 129999 || aliases.Contains(id)
                    || parts[2] != ((id / 1000) * 1000).ToString("D6")) continue;
                var name = names.GetValueOrDefault(id)?.Name;
                rows.Add(new(id, string.IsNullOrWhiteSpace(name) ? "Texto sin catalogar" : name,
                    RelativePath: relative, OriginalName: names.GetValueOrDefault(id)?.OriginalName ?? ""));
            }
            this.previewBanners = rows.OrderBy(row => row.Id).ToArray();
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
            this.previewCatalogError = $"No existe la textura {this.previewId} en el LP activo.";
            return;
        }
        this.previewCatalogError = null;
        var relative = UseNativeHd() ? row.RelativePath.Replace(".tex", "_hr1.tex") : row.RelativePath;
        var translated = Path.Combine(this.config.LanguagePackPath, relative);
        this.previewCancellation = new CancellationTokenSource();
        this.loadedPreviewId = row.Id;
        var token = this.previewCancellation.Token;
        this.previewTexture = this.previewOriginal ? Original() : Translation();
        this.previewNeedsShow = true;

        async Task<IDalamudTextureWrap> Original()
        {
            // Read the game archive directly. Shared texture lookups can apply substitutions.
            var file = await Task.Run(() => this.previewData.GetFile<TexFile>(relative), token)
                ?? throw new FileNotFoundException("Original texture not found in the game archive.", relative);
            token.ThrowIfCancellationRequested();
            return await this.textures.CreateFromTexFileAsync(file, "Gubal Dev original: " + relative, token);
        }

        async Task<IDalamudTextureWrap> Translation()
        {
            var bytes = await File.ReadAllBytesAsync(translated, token);
            return await this.textures.CreateFromImageAsync(bytes, "Gubal Dev translation: " + relative, token);
        }
    }

    private static unsafe bool UseNativeHd()
    {
        var stage = AtkStage.Instance();
        return stage != null && stage->AtkTextureResourceManager != null
            && stage->AtkTextureResourceManager->DefaultTextureScale == 2;
    }

    private unsafe void UpdateDevPreview()
    {
        if (!this.previewNeedsShow || this.previewTexture is null) return;
        if (this.previewTexture.IsFaulted)
        {
            this.previewCatalogError = this.previewTexture.Exception!.GetBaseException().Message;
            this.previewNeedsShow = false;
            return;
        }
        if (!this.previewTexture.IsCompletedSuccessfully) return;
        try
        {
            var texture = this.previewTexture.Result;
            if (texture.Width is <= 0 or > ushort.MaxValue || texture.Height is <= 0 or > ushort.MaxValue)
                throw new InvalidOperationException("Invalid native preview dimensions.");
            var ui = UIModule.Instance();
            if (ui == null || this.canPreview?.Invoke() != true) return;
            this.nativePreviewWidth = texture.Width;
            this.nativePreviewHeight = texture.Height;
            this.nativePreviewTexture = this.textures.ConvertToKernelTexture(texture, leaveWrapOpen: true);
            if (this.nativePreviewTexture == 0) throw new InvalidOperationException("Native preview texture creation failed.");
            this.previewNeedsShow = false;
            ui->ShowImage((uint)this.loadedPreviewId, true, 0, false);
        }
        catch (Exception error)
        {
            this.previewCatalogError = error.Message;
            this.previewNeedsShow = false;
        }
    }

    private unsafe void PrepareDevPreview(AddonEvent _, AddonArgs args)
    {
        if (this.nativePreviewTexture == 0) return;
        var addon = (AddonImage*)args.Addon.Address;
        if (addon == null || addon->ImageNode == null) return;
        var node = addon->ImageNode;
        if (node->PartsList == null || node->PartsList->Parts == null || node->PartId >= node->PartsList->PartCount) return;
        var part = &node->PartsList->Parts[node->PartId];
        var asset = part->UldAsset;
        if (asset == null || asset->AtkTexture.TextureType != TextureType.Resource
            || asset->AtkTexture.Resource == null || asset->AtkTexture.Resource->IconId != this.loadedPreviewId) return;
        var targetHeight = (int)Math.Round(node->AtkResNode.Width * (double)this.nativePreviewHeight / this.nativePreviewWidth);
        if (targetHeight is <= 0 or > ushort.MaxValue) return;
        this.previewNode = (nint)node;
        this.previewPart = (nint)part;
        this.previewAsset = (nint)asset;
        this.savedPreviewTexture = asset->AtkTexture;
        this.savedPreviewPart = *part;
        this.savedPreviewHeight = node->AtkResNode.Height;
        this.savedPreviewY = node->AtkResNode.Y;
        // Borrow the texture for this draw only. Do not change the shared game resource.
        asset->AtkTexture.KernelTexture = (KernelTexture*)this.nativePreviewTexture;
        asset->AtkTexture.TextureType = TextureType.KernelTexture;
        part->U = part->V = 0;
        part->Width = (ushort)this.nativePreviewWidth;
        part->Height = (ushort)this.nativePreviewHeight;
        node->AtkResNode.SetPositionFloat(node->AtkResNode.X,
            node->AtkResNode.Y + (node->AtkResNode.Height - targetHeight) / 2f);
        node->AtkResNode.SetHeight((ushort)targetHeight);
    }

    private unsafe void RestoreDevPreview(AddonEvent _, AddonArgs args)
        => this.RestoreNativePreview((AddonImage*)args.Addon.Address);

    private unsafe void RestoreNativePreview(AddonImage* addon)
    {
        if (this.previewNode == 0) return;
        if (addon != null && (nint)addon->ImageNode == this.previewNode
            && addon->ImageNode->PartsList != null && addon->ImageNode->PartId < addon->ImageNode->PartsList->PartCount
            && (nint)(&addon->ImageNode->PartsList->Parts[addon->ImageNode->PartId]) == this.previewPart)
        {
            var part = (AtkUldPart*)this.previewPart;
            if ((nint)part->UldAsset == this.previewAsset)
            {
                part->UldAsset->AtkTexture = this.savedPreviewTexture;
                *part = this.savedPreviewPart;
                addon->ImageNode->AtkResNode.SetHeight(this.savedPreviewHeight);
                addon->ImageNode->AtkResNode.SetPositionFloat(addon->ImageNode->AtkResNode.X, this.savedPreviewY);
            }
        }
        this.previewNode = this.previewPart = this.previewAsset = 0;
    }

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
        ImGui.TextWrapped("Vista nativa en pantalla. Original leído del juego y traducción leída del LP activo.");
        if (ImGui.Button("Recargar lista")) this.LoadDevBanners();
        ImGui.SameLine();
        ImGui.TextUnformatted($"{this.previewBanners.Length} rótulos");
        ImGui.InputText("Buscar", ref this.previewFilter, 200);
        ImGui.InputInt("ID de textura", ref this.previewId);
        if (ImGui.Button("Mostrar de nuevo")) this.previewRequested = true;
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
            if (ImGui.RadioButton($"{row.Name}##translated{row.Id}", this.previewId == row.Id && !this.previewOriginal))
            {
                this.previewId = row.Id;
                this.previewOriginal = false;
                this.previewRequested = true;
            }
        }
    }
}
