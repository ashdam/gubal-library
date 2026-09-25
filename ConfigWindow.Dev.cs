using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private readonly bool isDev;
    private readonly Func<bool>? canPreview;
    private int previewId = 128086;
    private static readonly (int Id, string Name)[] BozjaBanners =
    [
        (128086, "Frente Sur de Bozja"),
        (128090, "Acciones perdidas"),
        (128091, "Registro de campaña"),
        (128092, "Castrum Lacus Litore"),
        (128094, "Castrum Lacus Litore completado"),
        (128098, "Delubrum Reginae completado"),
        (128099, "Delubrum Reginae (Salvaje) completado"),
        (128110, "Delubrum Reginae"),
        (128111, "Delubrum Reginae (Salvaje)"),
        (128118, "Dalriada completado"),
        (128123, "Dalriada"),
        (128125, "Zadnor"),
        (128126, "Condecoraciones de la Resistencia"),
    ];

    private unsafe void ShowDevBanner()
    {
        if (!this.isDev || this.canPreview?.Invoke() != true || this.previewId is < 120000 or > 129999)
            return;
        var ui = UIModule.Instance();
        if (ui != null) ui->ShowImage((uint)this.previewId, true, 0, false);
    }

    private void DrawDevTab()
    {
        using var tab = ImRaii.TabItem("Dev");
        if (!tab) return;
        ImGui.TextWrapped("Vista nativa: el juego usa la textura del pack activo y elige su resolución.");
        ImGui.TextWrapped("Activa Menús e interfaz y reinicia el juego tras instalar nuevas texturas.");
        using var disabled = ImRaii.Disabled(this.canPreview?.Invoke() != true);
        ImGui.InputInt("ID de textura", ref this.previewId);
        if (ImGui.Button("Mostrar de nuevo")) this.ShowDevBanner();
        using var list = ImRaii.Child("##devBanners");
        if (!list) return;
        foreach (var (id, name) in BozjaBanners)
        {
            if (!ImGui.RadioButton($"{id} · {name}", this.previewId == id)) continue;
            this.previewId = id;
            this.ShowDevBanner();
        }
    }
}
