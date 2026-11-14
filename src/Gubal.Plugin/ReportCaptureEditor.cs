using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

internal sealed class ReportCaptureEditor : IDisposable
{
    private enum CaptureOperation { None, Crop, Redact, Restore }
    private readonly Configuration config;
    private readonly PluginUi ui;
    private readonly ITextureProvider textures;
    private readonly ReportCapture capture;
    private readonly Action requestCapture;
    private readonly Action openTab;
    private readonly Action<byte[]?, byte[]?> changed;
    private readonly Action<string?, Exception?> setError;
    private readonly CancellationTokenSource cancel = new();
    private IDalamudTextureWrap? screen;
    private IDalamudTextureWrap? preview;
    private Task<IDalamudTextureWrap>? captureTask;
    private Task<ReportCaptureResult>? cropTask;
    private Task<IDalamudTextureWrap>? restoreTask;
    private CaptureOperation captureOperation;
    private bool selecting;
    private bool dragging;
    private bool previewDragged;
    private string? previewDragOwner;
    private bool redactionArmed;
    private bool redactionGesture;
    private bool discardSelection;
    private Vector2 dragStart;
    private string? errorKey;
    private bool PreparingSelection => this.captureOperation == CaptureOperation.Crop;
    public bool Selecting => this.selecting;
    public bool Busy => this.captureTask is not null || this.cropTask is not null || this.restoreTask is not null;
    public byte[]? Image { get; private set; }
    public byte[]? OriginalImage { get; private set; }

    public ReportCaptureEditor(Configuration config, PluginUi ui, ITextureProvider textures,
        ITextureReadbackProvider readback, Action requestCapture, Action openTab,
        Action<byte[]?, byte[]?> changed, Action<string?, Exception?> setError)
    {
        this.config = config;
        this.ui = ui;
        this.textures = textures;
        this.capture = new ReportCapture(textures, readback, this.cancel.Token);
        this.requestCapture = requestCapture;
        this.openTab = openTab;
        this.changed = changed;
        this.setError = setError;
    }

    public void Restore(byte[] image, byte[]? original)
    {
        this.Image = image;
        this.OriginalImage = original;
        this.restoreTask = this.textures.CreateFromImageAsync(image, "Gubal report draft", this.cancel.Token);
    }

    public void BeginCapture()
    {
        this.redactionArmed = false;
        this.dragging = false;
        this.SetError(null);
        this.captureTask = this.textures.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
        {
            ViewportId = ImGui.GetMainViewport().ID,
            TakeBeforeImGuiRender = true,
            AutoUpdate = false,
        }, "Gubal report screenshot", this.cancel.Token);
    }

    public void ReleaseScreen()
    {
        if (this.Busy) return;
        this.screen?.Dispose(); this.screen = null;
    }

    public void Reset()
    {
        this.preview?.Dispose(); this.preview = null;
        this.ReleaseScreen();
        this.Image = this.OriginalImage = null;
        this.dragging = false; this.selecting = false;
        this.redactionArmed = false; this.redactionGesture = false; this.previewDragOwner = null;
        this.captureOperation = CaptureOperation.None; this.discardSelection = false;
    }

    private void SetError(string? key, Exception? error = null)
    {
        this.errorKey = key;
        this.setError(key, error);
    }

    public void CompleteTasks()
    {
        if (this.restoreTask?.IsCompleted is true)
        {
            try { this.preview = this.restoreTask.GetAwaiter().GetResult(); }
            catch (Exception error) { this.SetError("Draft", error); }
            this.restoreTask = null;
        }
        if (this.captureTask?.IsCompleted is true)
        {
            try
            {
                this.screen?.Dispose(); this.screen = this.captureTask.GetAwaiter().GetResult();
                this.selecting = true; this.dragging = false;
            }
            catch (Exception error) { this.SetError("CaptureError", error); this.openTab(); }
            this.captureTask = null;
        }
        if (this.cropTask?.IsCompleted is true)
        {
            try
            {
                var result = this.cropTask.GetAwaiter().GetResult();
                if (this.discardSelection) result.Texture.Dispose();
                else
                {
                    this.preview?.Dispose(); this.preview = result.Texture;
                    this.OriginalImage = this.captureOperation == CaptureOperation.Redact ? this.OriginalImage ?? this.Image : null;
                    this.Image = result.Png;
                    this.SetError(null);
                    this.changed(this.Image, this.OriginalImage);
                    if (this.PreparingSelection) { this.selecting = false; this.openTab(); }
                }
            }
            catch (Exception error)
            {
                if (!this.discardSelection)
                {
                    if (error is ReportCaptureException captureError)
                        this.SetError(captureError.Error == ReportCaptureError.Dimensions ? "ImageDimensions" : "ImageBytes");
                    else this.SetError("CaptureError", error);
                    if (this.PreparingSelection) { this.selecting = this.screen is not null; this.dragging = false; }
                }
            }
            if (this.discardSelection) { this.screen?.Dispose(); this.screen = null; }
            this.cropTask = null; this.captureOperation = CaptureOperation.None;
            this.discardSelection = false;
        }
    }

    public void DrawSelection()
    {
        if (this.screen is null) { this.selecting = false; return; }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin("###GubalReportSelection", flags))
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            ImGui.SetNextFrameWantCaptureKeyboard(true);
            ImGui.GetIO().WantCaptureMouse = true;
            ImGui.GetIO().WantCaptureKeyboard = true;
            ImGui.Image(this.screen.Handle, viewport.Size);
            var mouse = Vector2.Clamp((ImGui.GetMousePos() - viewport.Pos) / viewport.Size, Vector2.Zero, Vector2.One);
            if (!this.PreparingSelection && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                this.dragStart = mouse; this.dragging = true;
                if (this.errorKey is "ImageDimensions" or "ImageBytes" or "CaptureError") this.SetError(null);
            }
            var selectionTooLarge = false;
            if (this.dragging && !this.PreparingSelection)
            {
                var start = Vector2.Min(this.dragStart, mouse);
                var end = Vector2.Max(this.dragStart, mouse);
                var size = ReportCapture.Size(this.screen, start, end);
                selectionTooLarge = size.X > IssueReport.MaxImageDimension || size.Y > IssueReport.MaxImageDimension;
                ImGui.GetWindowDrawList().AddRect(viewport.Pos + start * viewport.Size, viewport.Pos + end * viewport.Size,
                    selectionTooLarge ? 0xff4d4df0 : 0xff5cb4d8, 0, ImDrawFlags.None, 2);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    this.dragging = false;
                    if (size.X >= 4 && size.Y >= 4)
                    {
                        if (selectionTooLarge)
                            this.SetError("ImageDimensions");
                        else
                        {
                            this.captureOperation = CaptureOperation.Crop;
                            this.discardSelection = false;
                            this.cropTask = this.capture.CropAsync(this.screen, start, end);
                        }
                    }
                }
            }
            var warning = selectionTooLarge || this.errorKey is "ImageDimensions" or "ImageBytes" or "CaptureError";
            var text = this.PreparingSelection ? T("PreparingCapture", "Preparing screenshot...")
                : selectionTooLarge || this.errorKey == "ImageDimensions"
                    ? T("ImageDimensions", "Select a smaller area. Width and height must not exceed 2048 pixels.")
                    : this.errorKey == "ImageBytes" ? T("ImageBytes", "The PNG exceeds 2 MiB. Select a smaller area.")
                    : this.errorKey == "CaptureError" ? T("CaptureError", "The screenshot could not be prepared. Try capturing or selecting the area again.")
                    : T("Select", "Drag to select the error. Escape cancels. The game keeps running.");
            DrawSelectionNotice(viewport, text, warning);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                this.selecting = false; this.dragging = false; this.openTab();
                this.discardSelection = this.PreparingSelection;
                if (!this.PreparingSelection) { this.screen.Dispose(); this.screen = null; }
                if (this.errorKey is "ImageDimensions" or "ImageBytes") this.SetError(null);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private static void DrawSelectionNotice(ImGuiViewportPtr viewport, string text, bool warning)
    {
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var padding = new Vector2(16, 12) * scale;
        var draw = ImGui.GetWindowDrawList();
        var icon = FontAwesomeIcon.ExclamationTriangle.ToIconString();
        var iconSize = Vector2.Zero;
        if (warning)
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            iconSize = ImGui.CalcTextSize(icon);
        }
        var iconWidth = warning ? iconSize.X + 10f * scale : 0;
        var wrapWidth = Math.Max(1, viewport.Size.X - 64f * scale - padding.X * 2 - iconWidth);
        var textSize = ImGui.CalcTextSize(text, false, wrapWidth);
        var size = new Vector2(textSize.X + iconWidth, Math.Max(textSize.Y, iconSize.Y)) + padding * 2;
        var position = viewport.Pos + new Vector2((viewport.Size.X - size.X) / 2, 20f * scale);
        var color = ImGui.GetColorU32(warning ? new Vector4(0.94f, 0.3f, 0.3f, 1) : Vector4.One);
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(new Vector4(0.04f, 0.055f, 0.095f, 0.96f)), 6f * scale);
        if (warning)
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            draw.AddText(position + padding, color, icon);
        }
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), position + padding + new Vector2(iconWidth, 0), color, text, wrapWidth);
    }

    public void DrawScreenshot(bool locked, float previewHeight, float? descriptionBottom = null)
    {
        PluginUi.DrawRequiredLabel(T("Screenshot", "Screenshot"));
        ImGui.SameLine();
        var limits = T("ScreenshotLimits", "PNG · 2 MiB · 2048 × 2048");
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(limits).X));
        ImGui.TextDisabled(limits);
        ImGui.Spacing();
        var previewStart = ImGui.GetCursorScreenPos();
        if (descriptionBottom is { } bottom) previewHeight = Math.Max(1, bottom - previewStart.Y);
        var previewArea = new Vector2(ImGui.GetContentRegionAvail().X, previewHeight);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(previewStart, previewStart + previewArea, ImGui.GetColorU32(ImGuiCol.FrameBg), 5);
        draw.AddRect(previewStart, previewStart + previewArea, ImGui.GetColorU32(ImGuiCol.Border), 5);
        if (this.preview is { } image)
        {
            var scale = Math.Min(1f, Math.Min(previewArea.X / image.Width, previewArea.Y / image.Height));
            var size = image.Size * scale;
            var position = previewStart + (previewArea - size) / 2;
            ImGui.SetCursorScreenPos(position);
            this.DrawCaptureImage("##reportPreview", image, size, locked, true);
        }
        else
        {
            using (ImRaii.Disabled(locked))
            {
                if (ImGui.InvisibleButton("##capturePlaceholder", previewArea)) this.requestCapture();
                if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            var label = T("CapturePlaceholder", "Click to capture the screen.");
            var keys = new List<string>();
            if (this.config.ReportHotkeyCtrl) keys.Add("Ctrl");
            if (this.config.ReportHotkeyShift) keys.Add("Shift");
            if (this.config.ReportHotkeyAlt) keys.Add("Alt");
            keys.Add($"F{Math.Clamp(this.config.ReportHotkeyFunction, 1, 12)}");
            this.ui.DrawCapturePlaceholder(previewStart, previewArea, label, string.Join(" + ", keys));
        }
        ImGui.SetCursorScreenPos(previewStart);
        ImGui.Dummy(previewArea);
        this.DrawOriginalCapture(locked);
        using var disabled = ImRaii.Disabled(locked);
        if (this.screen is not null || this.preview is not null)
        {
            var width = this.ImageToolsWidth(this.preview is not null ? 4 : 1, true);
            var height = PluginTheme.CompactActionHeight * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale
                + ImGui.GetStyle().ItemSpacing.Y;
            if (width > ImGui.GetContentRegionAvail().X) height += ImGui.GetStyle().ScrollbarSize;
            using var toolbar = ImRaii.Child("##captureActions", new Vector2(0, height), false,
                ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            if (toolbar)
            {
                if (this.screen is not null || this.preview is not null)
                {
                    this.AlignToolWidth(width);
                    if (this.ImageToolButton("recapture", FontAwesomeIcon.Camera, T("Recapture", "Recapture screenshot"), false, true)) this.requestCapture();
                }
                if (this.preview is not null)
                {
                    ImGui.SameLine();
                    if (this.ImageToolButton("remove", FontAwesomeIcon.TrashAlt, T("RemoveImage", "Remove image"), false, true))
                    {
                        this.preview.Dispose(); this.preview = null;
                        this.redactionArmed = false; this.dragging = false;
                        this.Image = null; this.OriginalImage = null;
                        this.changed(null, null);
                    }
                }
                if (this.preview is not null)
                {
                    ImGui.SameLine();
                    this.DrawCensorButtons(true);
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextWrapped(T("Public", "The report and screenshot will be public on GitHub. Hide player names and chat before sending."));
    }

    private void DrawCaptureImage(string id, IDalamudTextureWrap image, Vector2 size, bool locked, bool openOriginal)
    {
        var position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        ImGui.GetWindowDrawList().AddImage(image.Handle, position, position + size);
        var hovered = ImGui.IsItemHovered();
        if (hovered || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var uv = Vector2.Clamp((ImGui.GetMousePos() - position) / size, Vector2.Zero, Vector2.One);
        if (ImGui.IsItemActivated())
        {
            this.dragStart = uv; this.dragging = true; this.previewDragged = false;
            this.previewDragOwner = id;
            this.redactionGesture = this.redactionArmed && !locked;
        }
        if (this.dragging && this.previewDragOwner == id)
        {
            if (((uv - this.dragStart) * size).LengthSquared() > 16) this.previewDragged = true;
            var start = Vector2.Min(this.dragStart, uv); var end = Vector2.Max(this.dragStart, uv);
            if (this.previewDragged && this.redactionGesture && !locked)
                ImGui.GetWindowDrawList().AddRectFilled(position + start * size, position + end * size, 0xff000000);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                this.dragging = false; this.previewDragOwner = null;
                if (!this.redactionGesture && !this.previewDragged && openOriginal) ImGui.OpenPopup("###originalReportCapture");
                else if (this.redactionGesture && this.previewDragged && !locked
                    && (end.X - start.X) * image.Width >= 1 && (end.Y - start.Y) * image.Height >= 1)
                {
                    this.captureOperation = CaptureOperation.Redact;
                    this.cropTask = this.capture.RedactAsync(image, start, end);
                }
            }
        }
        if (hovered && !this.dragging && openOriginal)
            ImGui.SetTooltip(this.redactionArmed && !locked
                ? T("CensorHint", "Drag on the image to draw black rectangles. Select Censor area again to stop.")
                : T("ImageTooltip", "Click the image to view its original size."));
    }
    private float ImageToolsWidth(int count, bool compact) =>
        count * (compact ? PluginTheme.CompactActionHeight : PluginTheme.ActionHeight) * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale
        + Math.Max(0, count - 1) * ImGui.GetStyle().ItemSpacing.X;

    private void AlignToolWidth(float width) =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - width));

    private bool ImageToolButton(string id, FontAwesomeIcon icon, string tooltip, bool selected, bool compact)
    {
        var clicked = PluginUi.IconButton(id, icon, selected, compact);
        var showTooltip = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (showTooltip) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private string CensorLabel => this.redactionArmed ? T("CensorActive", "Censor area: ON") : T("Censor", "Censor area");

    private void DrawCensorButtons(bool compact = false)
    {
        if (this.ImageToolButton("censor", FontAwesomeIcon.Square, this.CensorLabel, this.redactionArmed, compact))
        {
            this.redactionArmed = !this.redactionArmed;
            this.dragging = false;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(this.OriginalImage is null))
        {
            if (this.ImageToolButton("clearCensorship", FontAwesomeIcon.Eraser, T("ClearCensorship", "Clear censored areas"), false, compact) && this.OriginalImage is { } original)
            {
                this.dragging = false;
                this.captureOperation = CaptureOperation.Restore;
                this.cropTask = this.capture.RestoreAsync(original);
            }
        }
    }

    private void DrawOriginalCapture(bool locked)
    {
        if (!ImGui.IsPopupOpen("###originalReportCapture")) return;
        if (this.preview is not { } image)
        {
            var close = true;
            if (ImGui.BeginPopupModal(T("OriginalCapture", "Capture at original size") + "###originalReportCapture", ref close))
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
            return;
        }
        var viewport = ImGui.GetMainViewport();
        var size = Vector2.Min(Vector2.Max(image.Size + new Vector2(48, 160), new Vector2(420, 260)), viewport.WorkSize * 0.9f);
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize / 2, ImGuiCond.Appearing, new Vector2(0.5f));
        var open = true;
        if (!ImGui.BeginPopupModal(T("OriginalCapture", "Capture at original size") + "###originalReportCapture", ref open,
                ImGuiWindowFlags.NoSavedSettings)) return;
        locked |= this.Busy;
        var hint = this.cropTask is not null ? T("Working", "Working...")
            : T("FullCaptureHint", "Select Censor area, then drag to draw black rectangles. Select it again to stop.");
        var width = this.ImageToolsWidth(2, false) + ImGui.GetStyle().ItemSpacing.X
            + this.ui.MeasureActionButton(T("ClosePreview", "Close preview"), false).X;
        var toolbarHeight = PluginTheme.ActionHeight * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale
            + ImGui.GetStyle().ItemSpacing.Y;
        if (width > ImGui.GetContentRegionAvail().X) toolbarHeight += ImGui.GetStyle().ScrollbarSize;
        var footerHeight = toolbarHeight + ImGui.CalcTextSize(hint, false, ImGui.GetContentRegionAvail().X).Y
            + ImGui.GetStyle().ItemSpacing.Y * 2;
        using (var content = ImRaii.Child("##originalCaptureImage", new Vector2(0, -footerHeight),
                   false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (content) this.DrawCaptureImage("##fullReportPreview", image, image.Size, locked, false);
        }
        ImGui.TextWrapped(hint);
        using (var toolbar = ImRaii.Child("##fullCaptureActions", new Vector2(0, toolbarHeight), false,
                   ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (toolbar)
            {
                this.AlignToolWidth(width);
                using (ImRaii.Disabled(locked)) this.DrawCensorButtons();
                ImGui.SameLine();
                if (this.ui.DrawActionButton(T("ClosePreview", "Close preview"), false))
                {
                    this.dragging = false; this.previewDragOwner = null;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            this.dragging = false; this.previewDragOwner = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private static string T(string key, string fallback) => Loc.Localize("Report." + key, fallback);

    public void Dispose()
    {
        this.cancel.Cancel();
        // GPU resources must outlive their pending readback operations.
        var pending = Task.WhenAll(new Task?[] { this.captureTask, this.cropTask, this.restoreTask }
            .OfType<Task>().Select(async task => { try { await task.ConfigureAwait(false); } catch { } }));
        _ = pending.ContinueWith(_ =>
        {
            if (this.captureTask?.IsCompletedSuccessfully is true) this.captureTask.Result.Dispose();
            if (this.cropTask?.IsCompletedSuccessfully is true) this.cropTask.Result.Texture.Dispose();
            if (this.restoreTask?.IsCompletedSuccessfully is true) this.restoreTask.Result.Dispose();
            this.screen?.Dispose(); this.preview?.Dispose(); this.cancel.Dispose();
        }, TaskScheduler.Default);
    }
}
