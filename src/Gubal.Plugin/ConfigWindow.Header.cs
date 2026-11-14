using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Glasswork.Backend;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private bool libraryCollapsed;
    private Vector2 expandedWindowSize;
    private Vector2? requestedWindowSize;
    private Task<IDalamudTextureWrap>? headerLogo;
    private int headerLogoSize;

    public void DisposeHeaderLogo()
    {
        if (this.headerLogo == null) return;
        _ = this.headerLogo.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) task.Result.Dispose();
            else _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        this.headerLogo = null;
    }

    private IDalamudTextureWrap GetHeaderLogo(IDalamudTextureWrap source, int size)
    {
        if (this.headerLogoSize != size)
        {
            this.DisposeHeaderLogo();
            this.headerLogoSize = size;
            this.headerLogo = this.ReduceHeaderLogo(source, size);
        }
        if (this.headerLogo is { IsCompletedSuccessfully: true } ready) return ready.Result;
        if (this.headerLogo is { IsFaulted: true } failed) _ = failed.Exception;
        return source;
    }

    private async Task<IDalamudTextureWrap> ReduceHeaderLogo(IDalamudTextureWrap source, int size)
    {
        IDalamudTextureWrap? reduced = null;
        try
        {
            // Reduce by at most half per pass to filter the source pixels.
            var width = source.Width;
            do
            {
                width = Math.Max(size, width / 2);
                var next = await this.textures.CreateFromExistingTextureAsync(reduced ?? source,
                    new TextureModificationArgs { NewWidth = width, NewHeight = width },
                    true, "Gubal header icon").ConfigureAwait(false);
                reduced?.Dispose();
                reduced = next;
            } while (width > size);
            var result = reduced;
            reduced = null;
            return result;
        }
        finally { reduced?.Dispose(); }
    }

    private void DrawLibraryBrand()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 48f * scale;
        var controlSize = 36f * scale;
        var controlsWidth = controlSize * 2 + ImGui.GetStyle().ItemSpacing.X;
        var draw = ImGui.GetWindowDrawList();
        var window = ImGui.GetWindowPos();
        var end = new Vector2(window.X + ImGui.GetWindowSize().X, start.Y + height);
        draw.AddRectFilled(window, end, ImGui.GetColorU32(ThemeColor(0x111820)), 10f * scale);
        draw.AddRectFilled(new Vector2(window.X, end.Y - 12f * scale), end, ImGui.GetColorU32(ThemeColor(0x111820)));
        ImGui.InvisibleButton("##libraryHeaderDrag", new Vector2(Math.Max(1, width - controlsWidth), height));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && !this.IsPinned)
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        const float logoSize = 42f;
        ImGui.SetCursorScreenPos(start + new Vector2(4f * scale, (height - logoSize * scale) / 2));
        if (this.textures.GetFromManifestResource(typeof(Plugin).Assembly, "GubalLibrary.images.icon.png").TryGetWrap(out var logo, out _))
        {
            var filtered = this.GetHeaderLogo(logo, Math.Max(1, (int)MathF.Ceiling(logoSize * scale)));
            DrawHeaderLogo(filtered.Handle, new Vector2(logoSize * scale), 11f * scale);
            ImGui.SameLine(0, 12f * scale);
        }
        var textX = ImGui.GetCursorScreenPos().X;
        using (this.ui.ActionFont.Push())
        {
            ImGui.SetCursorScreenPos(new Vector2(textX, start.Y + (height - ImGui.GetTextLineHeight()) / 2));
            DrawHeaderText("Gubal Library", start.Y + height / 2, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        }
        ImGui.SameLine(0, 12f * scale);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, start.Y + (height - ImGui.GetTextLineHeight()) / 2));
        DrawHeaderText(this.pluginVersion, start.Y + height / 2, Grey);
        this.DrawRestartBanner();
        var controls = new Vector2(start.X + width - controlsWidth, start.Y + (height - controlSize) / 2);
        if (this.DrawHeaderControl("##foldLibrary", controls, false))
        {
            if (!this.libraryCollapsed) this.expandedWindowSize = ImGui.GetWindowSize();
            this.libraryCollapsed = !this.libraryCollapsed;
            this.SizeConstraints = new()
            {
                MinimumSize = new Vector2(760, this.libraryCollapsed ? height / scale + ImGui.GetStyle().WindowPadding.Y * 2 / scale : 420),
            };
            this.Flags = this.libraryCollapsed ? this.Flags | ImGuiWindowFlags.NoResize : this.Flags & ~ImGuiWindowFlags.NoResize;
            this.requestedWindowSize = this.libraryCollapsed
                ? new Vector2(ImGui.GetWindowSize().X, height + ImGui.GetStyle().WindowPadding.Y * 2)
                : this.expandedWindowSize;
        }
        controls.X += controlSize + ImGui.GetStyle().ItemSpacing.X;
        if (this.DrawHeaderControl("##closeLibrary", controls, true)) this.IsOpen = false;
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height + ImGui.GetStyle().ItemSpacing.Y));
    }

    private static void DrawHeaderLogo(ImTextureID texture, Vector2 size, float radius)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var flags = draw.Flags;
        draw.Flags |= ImDrawListFlags.AntiAliasedFill | ImDrawListFlags.AntiAliasedLines;
        try
        {
            draw.AddImageRounded(texture, position, position + size, Vector2.Zero, Vector2.One,
                uint.MaxValue, radius);
            draw.AddRect(position, position + size, ImGui.GetColorU32(ThemeColor(0x111820)), radius,
                ImDrawFlags.None, 1f);
        }
        finally { draw.Flags = flags; }
        ImGui.Dummy(size);
    }

    private static void DrawHeaderText(string text, float centerY, Vector4 color)
    {
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var (top, bottom) = PluginUi.TextInkBounds(text);
        var position = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddText(font, fontSize,
            new Vector2(position.X, centerY - (top + bottom) / 2), ImGui.GetColorU32(color), text);
        ImGui.Dummy(ImGui.CalcTextSize(text));
    }

    private bool DrawHeaderControl(string id, Vector2 position, bool close)
    {
        ImGui.SetCursorScreenPos(position);
        using var scope = ImRaii.PushId(id);
        var icon = close ? FontAwesomeIcon.Times : this.libraryCollapsed ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown;
        var tooltip = close ? Loc.Localize("Report.Close", "Close")
            : this.libraryCollapsed ? Loc.Localize("Library.Expand", "Expand") : Loc.Localize("Library.Collapse", "Collapse");
        return Gw.IconButton(icon.ToIconString(), ButtonStyle.Plain, ButtonSize.Regular, tooltip,
            Glasswork.Color.FromVector4(Blue));
    }
}
