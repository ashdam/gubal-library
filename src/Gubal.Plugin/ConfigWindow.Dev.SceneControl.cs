using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private nint screenInfoRefresh;
    private nint finishScreenInfoChild;
    private nint freeScreenInfoNode;
    private int previewSceneMode;
    private int previewSceneIndex;
    private int[] previewSceneImageKeys = [];
    private readonly Dictionary<nint, nint> previewSceneChildren = [];
    private readonly Dictionary<nint, nint> previousSceneChildren = [];

    [StructLayout(LayoutKind.Explicit, Size = 0xA8)]
    private unsafe struct ScreenInfoRequest
    {
        [FieldOffset(0)] public int Index;
        [FieldOffset(4)] public uint Count;
        [FieldOffset(8)] public fixed byte Values[160];
    }

    private void ConfigureSceneControl(ISigScanner scanner)
    {
        scanner.TryScanText("4C 8B DC 55 57 41 54 48 81 EC 00 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 D0 00 00 00", out this.screenInfoRefresh);
        scanner.TryScanText("48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 48 63 DA 48 8B E9 48 8B BC D9 90 02 00 00", out this.finishScreenInfoChild);
        scanner.TryScanText("48 85 C9 0F 84 ?? ?? ?? ?? 48 89 4C 24 08 48 83 EC 28 33 D2 4C 8D 05", out this.freeScreenInfoNode);
    }

    private unsafe bool IsScreenInfo(AtkUnitBase* controller)
        // Lifecycle hooks can replace vtable entries. Check the named controller and native signatures.
        => controller != null && this.screenInfoRefresh != 0 && this.finishScreenInfoChild != 0 && this.freeScreenInfoNode != 0
            && controller->NameString is "_ScreenInfoFront" or "_ScreenInfoBack";

    private unsafe void BeginSceneRequest(int mode, int index, int[] images)
    {
        if (this.screenInfoRefresh == 0 || this.finishScreenInfoChild == 0 || this.freeScreenInfoNode == 0)
            throw new InvalidOperationException("Native scene cancellation is unavailable.");
        if (mode is 6 or 10 or 11)
        {
            var controller = (AtkUnitBase*)this.previewGui!.GetAddonByName("_ScreenInfoFront").Address;
            var child = this.IsScreenInfo(controller)
                ? *(nint*)((byte*)controller + 0x290 + index * 8) : 0;
            if (child == 0 || *(int*)((byte*)child + 0x258) != index)
                throw new InvalidOperationException("The native location title component is not available. Restart the game client to restore it.");
        }
        this.previewSceneMode = mode;
        this.previewSceneIndex = index;
        this.previewSceneImageKeys = images;
        this.previousSceneChildren.Clear();
        foreach (var name in new[] { "_ScreenInfoFront", "_ScreenInfoBack" })
        {
            var controller = (AtkUnitBase*)this.previewGui!.GetAddonByName(name).Address;
            if (this.IsScreenInfo(controller))
                this.previousSceneChildren[(nint)controller] = *(nint*)((byte*)controller + 0x290 + index * 8);
        }
    }

    private unsafe void CaptureSceneChildren()
    {
        foreach (var name in new[] { "_ScreenInfoFront", "_ScreenInfoBack" })
        {
            var controller = (AtkUnitBase*)this.previewGui!.GetAddonByName(name).Address;
            if (!this.IsScreenInfo(controller)) continue;
            var child = *(nint*)((byte*)controller + 0x290 + this.previewSceneIndex * 8);
            if (child != 0 && child != this.previousSceneChildren.GetValueOrDefault((nint)controller))
                this.previewSceneChildren[(nint)controller] = child;
        }
    }

    private unsafe void RememberSceneChild(AtkUnitBase* child)
    {
        foreach (var name in new[] { "_ScreenInfoFront", "_ScreenInfoBack" })
        {
            var controller = (AtkUnitBase*)this.previewGui!.GetAddonByName(name).Address;
            if (this.IsScreenInfo(controller)
                && *(nint*)((byte*)controller + 0x290 + this.previewSceneIndex * 8) == (nint)child)
                this.previewSceneChildren[(nint)controller] = (nint)child;
        }
    }

    private unsafe bool IsSceneChild(AtkUnitBase* child)
    {
        if (this.previewSceneImageKeys.Length == 0 || this.previewSceneIndex is < 0 or >= 69) return false;
        foreach (var name in new[] { "_ScreenInfoFront", "_ScreenInfoBack" })
        {
            var controller = (AtkUnitBase*)this.previewGui!.GetAddonByName(name).Address;
            if (this.IsScreenInfo(controller)
                && *(nint*)((byte*)controller + 0x290 + this.previewSceneIndex * 8) == (nint)child)
                return true;
        }
        return false;
    }

    private unsafe bool IsPreviewRequest(ScreenInfoRequest* request)
    {
        if (request->Index != this.previewSceneIndex || request->Count is < 3 or > 10) return false;
        var values = (AtkValue*)request->Values;
        if (values[0].Type != AtkValueType.Int || values[0].Int != this.previewSceneMode) return false;
        for (var index = 2; index < request->Count; index++)
            if (values[index].Type is AtkValueType.Int or AtkValueType.UInt
                && this.previewSceneImageKeys.Contains(values[index].Int)) return true;
        return false;
    }

    private unsafe void RemovePreviewRequests(StdList<ScreenInfoRequest>* requests)
    {
        var head = *(FFXIVClientStructs.STD.ContainerInterface.IStdList<ScreenInfoRequest>.Node**)requests;
        if (head == null) return;
        for (var node = head->Next; node != head;)
        {
            var next = node->Next;
            if (this.IsPreviewRequest(&node->Value))
            {
                var values = (AtkValue*)node->Value.Values;
                for (var index = 9; index >= 0; index--) values[index].Dtor();
                this.RemoveSceneNode((nint)requests, (nint)node, 0xB8);
            }
            node = next;
        }
    }

    private unsafe void RemoveSceneNode(nint list, nint node, ulong size)
    {
        var links = (nint*)node;
        *(nint*)links[1] = links[0];
        *((nint*)links[0] + 1) = links[1];
        --*((ulong*)list + 1);
        // ScreenInfo nodes use the game's small-block pool. Do not use StdList.Remove or IMemorySpace.Free.
        var free = (delegate* unmanaged<nint, ulong, uint, void>)this.freeScreenInfoNode;
        free(node, size, 0x190);
    }

    private unsafe void CancelSceneRequest()
    {
        if (this.previewSceneImageKeys.Length == 0 || this.previewGui == null) return;
        foreach (var name in new[] { "_ScreenInfoFront", "_ScreenInfoBack" })
        {
            var controller = (AtkUnitBase*)this.previewGui.GetAddonByName(name).Address;
            if (!this.IsScreenInfo(controller)) continue;
            // Remove only this preview's requests. Other game notifications keep their queues.
            this.RemovePreviewRequests((StdList<ScreenInfoRequest>*)((byte*)controller + 0x4B8));
            for (var group = 0; group < 5; group++)
                this.RemovePreviewRequests((StdList<ScreenInfoRequest>*)((byte*)controller + 0x4D0 + group * 24));
            var child = *(nint*)((byte*)controller + 0x290 + this.previewSceneIndex * 8);
            if (child == 0 || child != this.previewSceneChildren.GetValueOrDefault((nint)controller)) continue;
            foreach (var offset in new[] { 0x270, 0x280 })
            {
                var active = (StdList<int>*)((byte*)controller + offset);
                var head = *(FFXIVClientStructs.STD.ContainerInterface.IStdList<int>.Node**)active;
                if (head == null) continue;
                for (var node = head->Next; node != head;)
                {
                    var next = node->Next;
                    if (node->Value == this.previewSceneIndex) this.RemoveSceneNode((nint)active, (nint)node, 0x18);
                    node = next;
                }
            }
            for (var group = 0; group < 5; group++)
            {
                var active = (int*)((byte*)controller + 0x4C8 + group * 24);
                if (*active == this.previewSceneIndex) *active = 69;
            }
            // Native completion keeps persistent children and releases temporary children.
            var finish = (delegate* unmanaged<AtkUnitBase*, int, void>)this.finishScreenInfoChild;
            finish(controller, this.previewSceneIndex);
        }
        this.previewSceneChildren.Clear();
        this.previousSceneChildren.Clear();
        this.previewSceneImageKeys = [];
    }
}
