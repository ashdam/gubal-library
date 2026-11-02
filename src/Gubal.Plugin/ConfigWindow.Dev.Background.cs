using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private nint previewFadeManager;
    private float previewFadeCurrent;
    private Vector4 previewFadeColor;
    private nint previewUiManager;
    private UiFlags previewHiddenUi;
    private uint? previewEpilogueFrame;
    private string? previewBackgroundStatus;

    // Cut-screen epilogues use ImageNextPreview or the BossName closing-title mode.
    private bool IsEpiloguePreview => this.previewScene is { Mode: 3, Index: 6 }
        or { CutScreenImageId: > 0, Mode: 46, Index: 42 };

    private unsafe void RecordEpilogueDraw(AtkUnitBase* addon)
    {
        if (!this.IsEpiloguePreview || !this.IsSceneChild(addon)) return;
        var framework = Framework.Instance();
        if (framework != null) this.previewEpilogueFrame = framework->FrameCounter;
    }

    public override void OnClose()
    {
        this.RestorePreviewBackground();
        base.OnClose();
    }

    private unsafe void UpdatePreviewBackground()
    {
        var framework = Framework.Instance();
        if (!this.IsEpiloguePreview || this.previewNeedsShow || framework == null
            || this.previewEpilogueFrame is not { } frame
            || unchecked(framework->FrameCounter - frame) > 1)
        {
            this.RestorePreviewBackground();
            this.previewBackgroundStatus = this.IsEpiloguePreview
                ? "Black background: waiting for the native epilogue draw." : null;
            return;
        }

        if (this.previewFadeManager != 0) return;
        var manager = framework->EnvironmentManager;
        var units = RaptureAtkUnitManager.Instance();
        // Do not replace a fade that the game controls.
        if (manager == null || units == null)
        {
            this.previewBackgroundStatus = "Black background: native managers are unavailable.";
            return;
        }
        if (manager->FadeActive || manager->FadeDelay > 0
            || manager->FadeColor.W - manager->FadeCurrent > 0)
        {
            this.previewBackgroundStatus = $"Black background: the game controls a fade (active={manager->FadeActive}, delay={manager->FadeDelay:G3}, opacity={manager->FadeColor.W - manager->FadeCurrent:G3}).";
            return;
        }
        this.previewFadeManager = (nint)manager;
        this.previewFadeCurrent = manager->FadeCurrent;
        this.previewFadeColor = manager->FadeColor;
        manager->FadeColor = new Vector4 { W = 1 };
        manager->FadeCurrent = 0;
        this.previewUiManager = (nint)units;
        this.previewHiddenUi = (UiFlags.Hud | UiFlags.Nameplates | UiFlags.Chat
            | UiFlags.ActionBars | UiFlags.TargetInfo) & ~units->UiFlags;
        if (this.previewHiddenUi != UiFlags.None)
            this.SetPreviewHudVisibility(units, false);
        this.previewBackgroundStatus = "Black background: active. Game HUD hidden.";
    }

    private unsafe void SetPreviewHudVisibility(RaptureAtkUnitManager* units, bool visible)
    {
        var protectedAddons = new Dictionary<nint, bool>();
        void Protect(nint address)
        {
            if (address == 0 || protectedAddons.ContainsKey(address)) return;
            var addon = (AtkUnitBase*)address;
            protectedAddons.Add(address, addon->IgnoreUIDisplayMode);
            addon->IgnoreUIDisplayMode = true;
        }

        try
        {
            // Keep preview controllers and their scene outside HUD visibility changes.
            foreach (var name in new[] { "_ScreenInfoFront", "_ScreenInfoBack" })
            {
                var controller = (AtkUnitBase*)this.previewGui!.GetAddonByName(name).Address;
                if (!this.IsScreenInfo(controller)) continue;
                Protect((nint)controller);
                if (this.previewSceneIndex is < 0 or >= 69) continue;
                var child = *(nint*)((byte*)controller + 0x290 + this.previewSceneIndex * 8);
                if (child == this.previewSceneChildren.GetValueOrDefault((nint)controller)) Protect(child);
            }
            units->ToggleUi(this.previewHiddenUi, visible, true, true);
        }
        finally
        {
            foreach (var (address, ignoreUiDisplayMode) in protectedAddons)
                ((AtkUnitBase*)address)->IgnoreUIDisplayMode = ignoreUiDisplayMode;
        }
    }

    private unsafe void RestorePreviewBackground()
    {
        var units = RaptureAtkUnitManager.Instance();
        if (this.previewUiManager != 0 && (nint)units == this.previewUiManager
            && this.previewHiddenUi != UiFlags.None)
            this.SetPreviewHudVisibility(units, true);
        this.previewUiManager = 0;
        this.previewHiddenUi = UiFlags.None;
        if (this.previewFadeManager == 0) return;
        var framework = Framework.Instance();
        var manager = framework == null ? null : framework->EnvironmentManager;
        // Restore only the overlay that this preview owns.
        if ((nint)manager == this.previewFadeManager && !manager->FadeActive
            && manager->FadeDelay <= 0 && manager->FadeCurrent == 0
            && manager->FadeColor.X == 0 && manager->FadeColor.Y == 0
            && manager->FadeColor.Z == 0 && manager->FadeColor.W == 1)
        {
            manager->FadeCurrent = this.previewFadeCurrent;
            manager->FadeColor = this.previewFadeColor;
        }
        this.previewFadeManager = 0;
    }
}
