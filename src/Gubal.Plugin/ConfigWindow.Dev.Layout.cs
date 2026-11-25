using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private sealed record NativeCounter(uint ComponentNodeId, uint ValueNodeId, int Value, ushort TimelineLabel);
    private sealed record NativeLayout(string UldName, uint NodeId, uint[] ImageNodeIds, ushort TimelineLabel,
        uint? ContentNodeId, NativeCounter? Counter);
    private nint loadLayoutResources;
    private nint updateLayoutScale;
    private nint previewLayoutUnit;
    private long previewLayoutStarted;
    private long previewLayoutTick;
    private bool previewLayoutReady;
    private bool previewLayoutPlaying;
    private uint? previewLayoutFrame;
    private readonly Dictionary<nint, nint> previewLayoutImageParts = [];

    private struct LayoutImageParts
    {
        public AtkUldPartsList List;
        public AtkUldPart Part;
        public AtkUldAsset Asset;
    }

    private void ConfigureLayoutPreview(ISigScanner scanner)
    {
        scanner.TryScanText("40 53 48 83 EC 20 F6 81 86 00 00 00 01 48 8B D9 0F 84", out this.loadLayoutResources);
        scanner.TryScanText("40 53 48 83 EC 30 48 8B D9 0F 29 74 24 20 8B 89 C8 01 00 00", out this.updateLayoutScale);
    }

    private unsafe void BeginLayoutPreview(NativeLayout layout)
    {
        if (this.loadLayoutResources == 0 || this.updateLayoutScale == 0)
            throw new InvalidOperationException("Native layout loading is unavailable.");
        if (string.IsNullOrEmpty(layout.UldName) || !layout.UldName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            || layout.NodeId == 0)
            throw new InvalidDataException("Invalid native layout configuration.");
        var unit = IMemorySpace.GetUISpace()->Create<AtkUnitBase>();
        if (unit == null) throw new OutOfMemoryException();
        this.previewLayoutUnit = (nint)unit;
        this.previewLayoutReady = this.previewLayoutPlaying = false;
        this.previewLayoutFrame = null;
        this.previewLayoutStarted = this.previewLayoutTick = Stopwatch.GetTimestamp();
        unit->Initialize();
        unit->DisableAddonConfig = true;
        unit->DisableFocusability = true;
        unit->UldManager.InitializeResourceRendererManager();
        // The base widget loads visual resources without a game addon's callbacks or state.
        if (!unit->LoadUldByName(layout.UldName))
        {
            this.ReleaseLayoutPreview();
            throw new InvalidOperationException($"The game could not load layout {layout.UldName}.");
        }
    }

    private unsafe void DrawLayoutPreview()
    {
        if (this.previewLayoutUnit == 0 || this.previewScene?.Layout is not { } layout) return;
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (framework == null || this.previewLayoutFrame == framework->FrameCounter) return;
        // The native draw call can enter lifecycle listeners again.
        this.previewLayoutFrame = framework->FrameCounter;
        var unit = (AtkUnitBase*)this.previewLayoutUnit;
        try
        {
            if (!this.previewLayoutReady)
            {
                var load = (delegate* unmanaged<AtkUldManager*, void>)this.loadLayoutResources;
                load(&unit->UldManager);
                if (unit->UldManager.LoadedState == AtkLoadState.LoadError)
                    throw new InvalidDataException($"Native layout {layout.UldName} could not load its resources.");
                if (unit->UldManager.LoadedState != AtkLoadState.Loaded)
                {
                    this.CheckLayoutLoadTimeout();
                    return;
                }
                var objects = unit->UldManager.Objects;
                if (objects == null || objects->NodeCount < 1 || objects->NodeList == null)
                    throw new InvalidDataException("The native layout has no widget root.");
                unit->RootNode = objects->NodeList[0];
                unit->WidgetAlignment = ((AtkUldWidgetInfo*)objects)->WidgetAlignment;
                var branch = unit->GetNodeById(layout.NodeId);
                var content = unit->GetNodeById(layout.ContentNodeId ?? layout.NodeId);
                if (unit->RootNode == null || branch == null || content == null
                    || (layout.TimelineLabel != 0 && branch->Timeline == null)
                    || !IsLayoutDescendant(content, branch))
                    throw new InvalidDataException("The native layout does not contain the configured banner nodes.");
                foreach (var imageId in layout.ImageNodeIds)
                {
                    var image = unit->GetImageNodeById(imageId);
                    if (image == null || !IsLayoutDescendant(&image->AtkResNode, content))
                        throw new InvalidDataException("The native layout does not contain the configured banner images.");
                    this.PrepareLayoutImage(image);
                    image->LoadIconTexture((uint)this.previewId, (int)this.previewData!.Language + 2);
                }
                foreach (var node in unit->UldManager.Nodes)
                {
                    var current = node.Value;
                    if (current == null) continue;
                    if (IsLayoutDescendant(content, current))
                    {
                        current->ToggleVisibility(true);
                        if ((current != branch || layout.TimelineLabel == 0) && IsLayoutDescendant(branch, current))
                        {
                            current->DisableTimeline();
                            if (layout.TimelineLabel == 0 && current->Color.A == 0)
                                current->SetAlpha(255);
                        }
                    }
                }
                unit->Flags1A1 |= 1;
                unit->LoadState = AtkUnitBaseLoadState.FullyLoaded;
                unit->VisibilityState = AtkUnitBaseVisibilityState.Show | AtkUnitBaseVisibilityState.TransitionComplete;
                unit->Alpha = 255;
                unit->UldManager.UpdateDrawNodeList();
                this.previewLayoutReady = true;
            }
            var scale = (delegate* unmanaged<AtkUnitBase*, byte>)this.updateLayoutScale;
            scale(unit);
            // Base vfunc 25 applies the widget's ULD alignment through the game's viewport rules.
            var align = (delegate* unmanaged<AtkUnitBase*, void>)(*(nint**)unit)[25];
            align(unit);
            foreach (var imageId in layout.ImageNodeIds)
                if (!IsLayoutImageReady(unit->GetImageNodeById(imageId)))
                {
                    this.CheckLayoutLoadTimeout();
                    return;
                }
            var now = Stopwatch.GetTimestamp();
            if (!this.previewLayoutPlaying)
            {
                var branch = unit->GetNodeById(layout.NodeId);
                if (layout.TimelineLabel != 0)
                    branch->Timeline->PlayAnimation(AtkTimelineJumpBehavior.LoopForever, layout.TimelineLabel, 0, 0);
                if (layout.Counter is { } counter)
                    StartLayoutCounter(unit, unit->GetNodeById(layout.ContentNodeId ?? layout.NodeId), counter);
                this.previewLayoutPlaying = true;
                this.previewLayoutTick = now;
            }
            unit->Update((float)Math.Min(Stopwatch.GetElapsedTime(this.previewLayoutTick, now).TotalSeconds, 0.25));
            this.previewLayoutTick = now;
            HideOtherLayoutNodes(unit, unit->GetNodeById(layout.ContentNodeId ?? layout.NodeId));
            this.PrepareSceneTextures(unit);
            if (!this.nativeSceneSeen.Contains(this.previewId))
                throw new InvalidOperationException("The native layout texture could not be matched to the preview.");
            unit->Draw();
        }
        catch (Exception error)
        {
            this.previewCatalogError = error.Message;
            this.ReleaseLayoutPreview();
        }
        finally
        {
            this.RestoreSceneTextures((nint)unit);
        }
    }

    private static unsafe bool IsLayoutDescendant(AtkResNode* node, AtkResNode* ancestor)
    {
        for (var depth = 0; node != null && depth < 128; depth++, node = node->ParentNode)
            if (node == ancestor) return true;
        return false;
    }

    private static unsafe void StartLayoutCounter(AtkUnitBase* unit, AtkResNode* content, NativeCounter counter)
    {
        var node = unit->GetNodeById(counter.ComponentNodeId);
        if (node == null || (int)node->Type < 1000 || !IsLayoutDescendant(node, content)
            || node->ParentNode == null || node->ParentNode->Timeline == null)
            throw new InvalidDataException("The native layout does not contain the configured counter component.");
        var component = ((AtkComponentNode*)node)->Component;
        var valueNode = component == null ? null : component->GetNodeById(counter.ValueNodeId);
        if (valueNode == null || valueNode->Type != NodeType.Counter)
            throw new InvalidDataException("The native layout does not contain the configured counter node.");
        ((AtkCounterNode*)valueNode)->SetNumber(counter.Value);
        node->ParentNode->Timeline->PlayAnimation(AtkTimelineJumpBehavior.LoopForever, counter.TimelineLabel, 0, 0);
    }

    private static unsafe bool IsLayoutImageReady(AtkImageNode* image)
    {
        if (image == null || image->PartsList == null || image->PartId >= image->PartsList->PartCount) return false;
        var asset = image->PartsList->Parts[image->PartId].UldAsset;
        return asset != null && asset->AtkTexture.TextureType == TextureType.Resource
            && asset->AtkTexture.Resource != null && asset->AtkTexture.Resource->TexFileResourceHandle != null
            && asset->AtkTexture.Resource->TexFileResourceHandle->Texture != null;
    }

    private unsafe void PrepareLayoutImage(AtkImageNode* image)
    {
        // Dynamic image nodes need an owned texture slot.
        if (image->PartsList == null)
        {
            var parts = (LayoutImageParts*)IMemorySpace.GetUISpace()->Malloc<LayoutImageParts>();
            if (parts == null) throw new OutOfMemoryException();
            *parts = default;
            parts->Asset.AtkTexture.Ctor();
            this.previewLayoutImageParts.Add((nint)image, (nint)parts);
            parts->Part.UldAsset = &parts->Asset;
            parts->Part.Width = image->Width;
            parts->Part.Height = image->Height;
            parts->List.PartCount = 1;
            parts->List.Parts = &parts->Part;
            image->PartsList = &parts->List;
            image->PartId = 0;
        }
        if (image->PartsList->Parts == null || image->PartId >= image->PartsList->PartCount
            || image->PartsList->Parts[image->PartId].UldAsset == null)
            throw new InvalidDataException("The native image has no texture slot.");
    }

    private static unsafe void HideOtherLayoutNodes(AtkUnitBase* unit, AtkResNode* branch)
    {
        // Timelines can restore visibility. Isolate the banner after each native update.
        foreach (var node in unit->UldManager.Nodes)
        {
            var current = node.Value;
            if (current == null) continue;
            var excluded = !IsLayoutDescendant(current, branch) && !IsLayoutDescendant(branch, current);
            current->IsDrawDisabled = excluded;
            if (excluded) current->ToggleVisibility(false);
        }
    }

    private void CheckLayoutLoadTimeout()
    {
        if (Stopwatch.GetElapsedTime(this.previewLayoutStarted).TotalSeconds > 15)
            throw new InvalidOperationException("The native layout did not finish loading its banner texture.");
    }

    private unsafe void ReleaseLayoutPreview()
    {
        if (this.previewLayoutUnit == 0) return;
        var unit = (AtkUnitBase*)this.previewLayoutUnit;
        this.RestoreSceneTextures(this.previewLayoutUnit);
        foreach (var (imageAddress, partsAddress) in this.previewLayoutImageParts)
        {
            var parts = (LayoutImageParts*)partsAddress;
            var image = (AtkImageNode*)imageAddress;
            if (image->PartsList == &parts->List) image->PartsList = null;
            parts->Asset.AtkTexture.ReleaseTexture();
            IMemorySpace.Free(parts, (ulong)sizeof(LayoutImageParts));
        }
        this.previewLayoutImageParts.Clear();
        this.previewLayoutUnit = 0;
        // This widget is not in AtkUnitManager. Release only its owned visual resources.
        unit->UldManager.Finalizer();
        IMemorySpace.Free(unit, (ulong)sizeof(AtkUnitBase));
        this.previewLayoutReady = this.previewLayoutPlaying = false;
    }
}
