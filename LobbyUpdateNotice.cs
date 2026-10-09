using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

internal sealed class LobbyUpdateNotice(IDalamudPluginInterface plugin, ITextureProvider textures, IPluginLog log)
{
    private Task<PluginUpdate?>? check;
    private long nextCheck;
    private Version? dismissedVersion;
    private string? installerError;
    private Version? shownVersion;
    private bool showing;
    private bool showingPreview;
    private float elapsed;

    public bool Draw(bool loggedIn, bool preview)
    {
        if (loggedIn && !preview) return preview;
        if (!plugin.IsDev && Environment.TickCount64 >= this.nextCheck &&
            (this.check is null || this.check.IsCompleted))
        {
            this.nextCheck = Environment.TickCount64 + (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
            this.check = this.CheckAsync();
        }
        var update = this.check is { IsCompletedSuccessfully: true } ? this.check.Result : null;
        if (!preview && (update is null || update.Version == this.dismissedVersion))
        {
            this.showing = false;
            return false;
        }
        var version = update?.Version ?? plugin.Manifest.AssemblyVersion;
        if (!this.showing || this.showingPreview != preview || this.shownVersion != version)
        {
            this.showing = true;
            this.showingPreview = preview;
            this.shownVersion = version;
            this.elapsed = 0;
            this.installerError = null;
        }
        this.elapsed += ImGui.GetIO().DeltaTime;
        if (this.elapsed >= 10) return this.Dismiss(version, preview);

        var viewport = ImGui.GetMainViewport();
        var width = Math.Min(400 * ImGuiHelpers.GlobalScale, viewport.WorkSize.X - 32);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize - new Vector2(24 * ImGuiHelpers.GlobalScale),
            ImGuiCond.Always, Vector2.One);
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        var open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        var visible = ImGui.Begin($"Gubal Library ({plugin.Manifest.AssemblyVersion})###GubalPluginUpdate", ref open,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove);
        try
        {
            if (visible)
            {
                var closeWidth = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2;
                var headerPosition = ImGui.GetCursorPos();
                var headerWidth = ImGui.GetContentRegionAvail().X;
                ImGui.PushTextWrapPos(headerPosition.X + headerWidth - closeWidth - ImGui.GetStyle().ItemSpacing.X);
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.2f, 1f));
                ImGui.TextWrapped(string.Format(Loc.Localize("PluginUpdate.Available",
                    "{0} version update of Gubal Library is available"), version));
                ImGui.PopStyleColor();
                ImGui.PopTextWrapPos();
                var headerBottom = ImGui.GetCursorPosY();
                ImGui.SetCursorPos(new Vector2(headerPosition.X + headerWidth - closeWidth, headerPosition.Y));
                if (ImGui.SmallButton("X##closePluginUpdate")) open = false;
                ImGui.SetCursorPos(new Vector2(headerPosition.X, Math.Max(headerBottom, ImGui.GetCursorPosY())));
                if (textures.GetFromManifestResource(typeof(Plugin).Assembly, "GubalLibrary.images.icon.png")
                    .TryGetWrap(out var icon, out _))
                {
                    ImGui.Image(icon.Handle, new Vector2(48 * ImGuiHelpers.GlobalScale));
                    ImGui.SameLine();
                }
                ImGui.BeginGroup();
                if (ImGui.Button(Loc.Localize("PluginUpdate.OpenInstaller", "Open plugin catalog")))
                {
                    this.installerError = plugin.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, "Gubal")
                        ? null : Loc.Localize("PluginUpdate.InstallerError", "Could not open the Dalamud installer.");
                    if (this.installerError is null) open = false;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.Localize("PluginUpdate.Instructions",
                        "Open the Dalamud installer and update Gubal Library."));
                ImGui.TextWrapped(Loc.Localize("PluginUpdate.CatalogHint",
                    "In the catalog, click \"Update plugins\"."));
                ImGui.EndGroup();
                if (this.installerError is not null) ImGui.TextWrapped(this.installerError);
                ImGui.TextDisabled(string.Format(Loc.Localize("PluginUpdate.AutoClose", "Closes in {0} seconds"),
                    (int)Math.Ceiling(10 - this.elapsed)));
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.55f, 0.16f, 0.18f, 1f));
                ImGui.ProgressBar(1 - this.elapsed / 10, new Vector2(-1, 3 * ImGuiHelpers.GlobalScale), string.Empty);
                ImGui.PopStyleColor();
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar();
        }
        if (open) return preview;
        return this.Dismiss(version, preview);
    }

    private bool Dismiss(Version version, bool preview)
    {
        this.showing = false;
        if (!preview) this.dismissedVersion = version;
        return false;
    }

    private async Task<PluginUpdate?> CheckAsync()
    {
        try { return await plugin.CheckForUpdateAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            log.Warning(error, "Could not check for a Gubal plugin update.");
            return null;
        }
    }
}
