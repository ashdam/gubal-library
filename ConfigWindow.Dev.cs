using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private readonly bool isDev;
    private readonly Func<bool>? canPreview;
    private int previewId = 120052;
    private bool previewOriginal;
    private string previewFilter = string.Empty;
    private string previewFontFilter = string.Empty;
    private string? previewCatalogPath;
    private string? previewCatalogError;
    private BannerPreview[] previewBanners = [];
    private IAddonLifecycle? previewLifecycle;
    private uint activePreviewId;
    private readonly Dictionary<uint, long> previewRequests = [];
    private nint resizedPreviewNode;
    private ushort previewNodeHeight;
    private float previewNodeY;
    private uint locationPreviewSourceId;
    private uint locationPreviewTargetId;
    private long locationPreviewUntil;
    private nint locationPreviewAddon;
    private bool locationPreviewImagePending;

    public void ConfigureDevPreview(IAddonLifecycle lifecycle)
    {
        if (!this.isDev) return;
        this.previewLifecycle = lifecycle;
        lifecycle.RegisterListener(AddonEvent.PreDraw, "_Image", this.ResizeDevPreview);
        lifecycle.RegisterListener(AddonEvent.PostDraw, "_Image", this.RestoreDevPreview);
        lifecycle.RegisterListener(AddonEvent.PreSetup, "_ScreenInfoFront", this.SetLocationPreviewValues);
        lifecycle.RegisterListener(AddonEvent.PreRefresh, "_ScreenInfoFront", this.SetLocationPreviewValues);
        lifecycle.RegisterListener(AddonEvent.PostDraw, "_ScreenInfoFront", this.EndLocationPreview);
    }

    public void DisposeDevPreview()
    {
        this.previewLifecycle?.UnregisterListener(this.ResizeDevPreview, this.RestoreDevPreview,
            this.SetLocationPreviewValues, this.EndLocationPreview);
        this.previewLifecycle = null;
    }

    private unsafe void ResizeDevPreview(AddonEvent _, AddonArgs args)
    {
        if (this.previewRequests.Count == 0) return;
        var addon = (AddonImage*)args.Addon.Address;
        if (addon == null || addon->ImageNode == null) return;
        var node = addon->ImageNode;
        if (node->PartsList == null || node->PartsList->Parts == null ||
            node->PartId >= node->PartsList->PartCount) return;
        var asset = node->PartsList->Parts[node->PartId].UldAsset;
        if (asset == null || asset->AtkTexture.TextureType != TextureType.Resource ||
            asset->AtkTexture.Resource == null)
            return;
        var visibleId = asset->AtkTexture.Resource->IconId;
        var now = Environment.TickCount64;
        if (!this.previewRequests.TryGetValue(visibleId, out var until) || now > until) return;
        // The previous texture can remain visible while ShowImage changes the banner.
        this.previewRequests[visibleId] = now + 15000;
        var width = asset->AtkTexture.GetTextureWidth();
        var height = asset->AtkTexture.GetTextureHeight();
        if (width == 0 || height == 0) return;
        var image = &node->AtkResNode;
        var targetHeight = (int)Math.Round(image->Width * (double)height / width);
        if (targetHeight is <= 0 or > ushort.MaxValue || image->Height == targetHeight) return;
        // Keep native texture proportions in Dev without changing the source texture.
        this.resizedPreviewNode = (nint)image;
        this.previewNodeHeight = image->Height;
        this.previewNodeY = image->Y;
        image->SetPositionFloat(image->X, image->Y + (image->Height - targetHeight) / 2f);
        image->SetHeight((ushort)targetHeight);
    }

    private unsafe void RestoreDevPreview(AddonEvent _, AddonArgs args)
    {
        if (this.resizedPreviewNode == 0) return;
        var addon = (AddonImage*)args.Addon.Address;
        if (addon != null && addon->ImageNode != null &&
            (nint)(&addon->ImageNode->AtkResNode) == this.resizedPreviewNode)
        {
            var image = &addon->ImageNode->AtkResNode;
            image->SetHeight(this.previewNodeHeight);
            image->SetPositionFloat(image->X, this.previewNodeY);
        }
        this.resizedPreviewNode = 0;
    }

    private sealed record BannerPreview(int Id, string Name, string Resolutions, int OriginalId = 0,
        uint TerritoryId = 0, string Font = "");

    private unsafe void SetLocationPreviewValues(AddonEvent _, AddonArgs args)
    {
        if (!this.locationPreviewImagePending || Environment.TickCount64 > this.locationPreviewUntil) return;
        var (address, count) = args switch
        {
            AddonSetupArgs setup => (setup.AtkValues, setup.AtkValueCount),
            AddonRefreshArgs refresh => (refresh.AtkValues, refresh.AtkValueCount),
            _ => (nint.Zero, 0u),
        };
        if (address == 0 || count != 10) return;
        var values = (AtkValue*)address;
        if (values[0].Int is not (6 or 10) || values[6].Int != this.locationPreviewSourceId) return;
        // Native location titles use index 1 for the icon folder and index 6 for the lower image.
        values[1].Int = (int)IconSubFolder.English;
        values[6].Int = (int)this.locationPreviewTargetId;
        if (this.previewOriginal)
        {
            for (var index = 2; index < count; index++)
            {
                if (index == 6) continue;
                var header = this.previewBanners.FirstOrDefault(row => row.Font == "zone-headers" &&
                    row.Id == values[index].Int && row.OriginalId is >= 120000 and <= 129999);
                if (header != null) values[index].Int = header.OriginalId;
            }
        }
        this.locationPreviewAddon = args.Addon.Address;
        this.locationPreviewImagePending = false;
    }

    private unsafe void EndLocationPreview(AddonEvent _, AddonArgs args)
    {
        if (this.locationPreviewUntil == 0 || Environment.TickCount64 <= this.locationPreviewUntil) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || (nint)addon != this.locationPreviewAddon) return;
        if (addon->AtkValues != null && addon->AtkValuesCount > 6 &&
            addon->AtkValues[6].Int == this.locationPreviewTargetId)
            addon->Hide(false, false, 0);
        this.locationPreviewUntil = 0;
        this.locationPreviewAddon = 0;
        this.locationPreviewImagePending = false;
    }

    private void LoadDevBanners()
    {
        this.previewCatalogPath = this.config.LanguagePackPath;
        this.previewCatalogError = null;
        this.previewBanners = [];
        var path = Path.Combine(this.config.LanguagePackPath, "dev", "banner-previews.json");
        if (!File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            this.previewBanners = (JsonSerializer.Deserialize<BannerPreview[]>(stream) ?? [])
                .Where(row => row.Id is >= 120000 and <= 129999 && !string.IsNullOrWhiteSpace(row.Name))
                .DistinctBy(row => row.Id)
                .OrderBy(row => row.Id)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            this.previewCatalogError = error.Message;
        }
    }

    private unsafe void ShowDevBanner()
    {
        if (!this.isDev || this.canPreview?.Invoke() != true || this.previewId is < 120000 or > 129999)
            return;
        var ui = UIModule.Instance();
        if (ui == null) return;
        var banner = this.previewBanners.FirstOrDefault(row => row.Id == this.previewId);
        if (banner is { TerritoryId: > 0 })
        {
            this.locationPreviewSourceId = (uint)banner.Id;
            this.locationPreviewTargetId = (uint)(this.previewOriginal ? banner.OriginalId : banner.Id);
            if (this.locationPreviewTargetId is < 120000 or > 129999) return;
            this.locationPreviewUntil = Environment.TickCount64 + 6000;
            this.locationPreviewAddon = 0;
            this.locationPreviewImagePending = true;
            var language = 1;
            ui->ShowLocationTitle(banner.TerritoryId, true, false, &language);
            return;
        }
        var id = this.previewOriginal
            ? banner?.OriginalId ?? 0
            : this.previewId;
        if (id is < 120000 or > 129999) return;
        this.activePreviewId = (uint)id;
        var now = Environment.TickCount64;
        foreach (var expired in this.previewRequests.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray())
            this.previewRequests.Remove(expired);
        this.previewRequests[this.activePreviewId] = now + 15000;
        ui->ShowImage(this.activePreviewId, true, 0, false);
    }

    private unsafe void DrawDevTab()
    {
        using var tab = ImRaii.TabItem("Dev");
        if (!tab) return;
        ImGui.TextWrapped("Vista nativa: el juego usa la textura del pack activo y elige su resolución.");
        ImGui.TextWrapped("Activa Menús e interfaz y reinicia el juego tras instalar nuevas texturas.");
        if (this.previewCatalogPath != this.config.LanguagePackPath) this.LoadDevBanners();
        if (ImGui.Button("Recargar lista")) this.LoadDevBanners();
        ImGui.SameLine();
        ImGui.TextUnformatted($"{this.previewBanners.Length} rótulos");
        ImGui.InputText("Buscar", ref this.previewFilter, 200);
        if (ImGui.BeginCombo("Fuente", this.previewFontFilter.Length == 0 ? "Todas" : this.previewFontFilter))
        {
            foreach (var font in new[] { "", "Zonas (todas)" }.Concat(this.previewBanners
                         .Select(row => row.Font).Where(font => !string.IsNullOrEmpty(font))
                         .Distinct().Order(StringComparer.OrdinalIgnoreCase)))
            {
                if (ImGui.Selectable(font.Length == 0 ? "Todas" : font, this.previewFontFilter == font))
                    this.previewFontFilter = font;
            }
            ImGui.EndCombo();
        }
        var visible = this.previewBanners.Where(row =>
            (this.previewFontFilter.Length == 0 || row.Font == this.previewFontFilter ||
             this.previewFontFilter == "Zonas (todas)" && row.Font.StartsWith("zone-", StringComparison.Ordinal)) &&
            (this.previewFilter.Length == 0 || $"{row.Id} {row.Name} {row.Font}"
                .Contains(this.previewFilter, StringComparison.OrdinalIgnoreCase))).ToArray();
        ImGui.TextUnformatted($"{visible.Length} visibles de {this.previewBanners.Length}");
        if (this.previewCatalogError != null) ImGui.TextWrapped(this.previewCatalogError);
        using var disabled = ImRaii.Disabled(this.canPreview?.Invoke() != true);
        ImGui.InputInt("ID de textura", ref this.previewId);
        if (ImGui.Button("Mostrar de nuevo")) this.ShowDevBanner();
        using var list = ImRaii.Child("##devBanners");
        if (!list) return;
        using var table = ImRaii.Table("##devBannerComparison", 3,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg | ImGuiTableFlags.Sortable);
        if (!table) return;
        ImGui.TableSetupColumn("Fuente", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("Original del juego");
        ImGui.TableSetupColumn("Traducción");
        ImGui.TableHeadersRow();
        var sort = ImGui.TableGetSortSpecs();
        IEnumerable<BannerPreview> ordered = visible.OrderBy(row => row.Id);
        if (sort.SpecsCount > 0)
        {
            var spec = sort.Specs[0];
            var descending = spec.SortDirection == ImGuiSortDirection.Descending;
            ordered = spec.ColumnIndex switch
            {
                0 => descending ? visible.OrderByDescending(row => row.Font, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id)
                    : visible.OrderBy(row => row.Font, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id),
                2 => descending ? visible.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id)
                    : visible.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id),
                _ => descending ? visible.OrderByDescending(row => row.Id) : visible.OrderBy(row => row.Id),
            };
            sort.SpecsDirty = false;
        }
        foreach (var row in ordered)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(row.Font) ? "Sin clasificar" : row.Font);
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(row.OriginalId is < 120000 or > 129999))
            {
                if (ImGui.RadioButton($"{row.Id} · Original##original{row.Id}",
                        this.previewId == row.Id && this.previewOriginal))
                {
                    this.previewId = row.Id;
                    this.previewOriginal = true;
                    this.ShowDevBanner();
                }
            }
            ImGui.TextWrapped(row.Resolutions ?? string.Empty);
            ImGui.TableNextColumn();
            if (ImGui.RadioButton($"##translated{row.Id}", this.previewId == row.Id && !this.previewOriginal))
            {
                this.previewId = row.Id;
                this.previewOriginal = false;
                this.ShowDevBanner();
            }
            ImGui.SameLine();
            ImGui.TextWrapped(row.Name);
        }
    }
}
