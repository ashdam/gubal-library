using System.Globalization;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina;
using Lumina.Excel.Sheets;

namespace GubalLibrary;

internal sealed unsafe class EnglishItemNames : IDisposable
{
    private const uint NodeId = 0x47554241;
    private readonly Configuration config;
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly IContextMenu contextMenu;
    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<AgentItemDetail.Delegates.OnItemHovered>? hoverHook;
    private uint hoveredBaseId;
    private uint hoveredGlamourId;
    private GameData? original;
    private AddonItemDetail* owner;
    private AtkTextNode* english;
    private AtkTextNode* modifiedName;
    private byte[] originalText = [];
    private byte[] renderedText = [];
    private byte originalFont;
    private byte originalAlignment;
    private ushort originalHeight;
    private float originalY;
    private TextFlags originalFlags;
    private uint lastItemId;
    private string lastName = string.Empty;
    private bool failed;
    private bool disposed;

    public EnglishItemNames(Configuration config, IDataManager data, IGameGui gameGui,
        IAddonLifecycle lifecycle, IFramework framework, IGameInteropProvider interop,
        IContextMenu contextMenu, IPluginLog log)
    {
        this.config = config;
        this.data = data;
        this.gameGui = gameGui;
        this.contextMenu = contextMenu;
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.log = log;
        lifecycle.RegisterListener(AddonEvent.PreDraw, "ItemDetail", this.Prepare);
        lifecycle.RegisterListener(AddonEvent.PreUpdate, "ItemDetail", this.Restore);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemDetail", this.Finalize);
        contextMenu.OnMenuOpened += this.OnMenuOpened;
        try
        {
            this.hoverHook = interop.HookFromAddress<AgentItemDetail.Delegates.OnItemHovered>(
                AgentItemDetail.Addresses.OnItemHovered.Value, this.OnItemHovered);
            this.hoverHook.Enable();
        }
        catch (Exception error)
        {
            this.hoverHook?.Dispose();
            this.hoverHook = null;
            log.Warning(error, "Original glamour names are unavailable.");
        }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.contextMenu.OnMenuOpened -= this.OnMenuOpened;
        this.hoverHook?.Dispose();
        this.lifecycle.UnregisterListener(this.Prepare, this.Restore, this.Finalize);
        if (this.framework.IsInFrameworkUpdateThread) this.Cleanup();
        else _ = this.framework.RunOnFrameworkThread(this.Cleanup);
    }

    private void Cleanup()
    {
        this.Release((AddonItemDetail*)this.gameGui.GetAddonByName("ItemDetail").Address);
        this.original?.Dispose();
        this.original = null;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (this.disposed || args.Target is not MenuTargetInventory inventory
            || inventory.TargetItem is not { } item || item.ItemId == 0) return;
        try
        {
            var name = this.OriginalName(item.ItemId);
            if (name.Length == 0) return;
            args.AddMenuItem(new MenuItem
            {
                Name = Loc.Localize("Items.CopyEnglishName", "Copy English name"),
                PrefixChar = 'G',
                Priority = int.MaxValue,
                OnClicked = _ => ImGui.SetClipboardText(name),
            });
        }
        catch (Exception error)
        {
            this.log.Error(error, "Could not add the original item name to the context menu.");
        }
    }

    private bool OnItemHovered(AgentItemDetail* agent, InventoryItem** outItem,
        InventoryType* container, short* slot, uint itemId, uint rowIdOrIndex, InventoryItem* item)
    {
        var result = this.hoverHook!.Original(agent, outItem, container, slot, itemId, rowIdOrIndex, item);
        this.hoveredBaseId = 0;
        this.hoveredGlamourId = 0;
        if (!this.config.ShowEnglishItemNames || !result || outItem == null || *outItem == null) return result;
        this.hoveredBaseId = ItemUtil.GetBaseId((*outItem)->GetItemId()).ItemId;
        this.hoveredGlamourId = (*outItem)->GetGlamourId();
        return result;
    }

    internal string OriginalName(uint rawId)
    {
        if (rawId == this.lastItemId) return this.lastName;
        // Shared Dalamud sheets can contain the language pack. Read the original archives.
        this.original ??= new GameData(this.data.GameData.DataPath.FullName,
            new LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var (id, kind) = ItemUtil.GetBaseId(rawId);
        var name = kind == ItemKind.EventItem
            ? this.original.GetExcelSheet<EventItem>(Lumina.Data.Language.English)?.GetRowOrDefault(id)?.Name.ExtractText()
            : this.original.GetExcelSheet<Item>(Lumina.Data.Language.English)?.GetRowOrDefault(id)?.Name.ExtractText();
        this.lastItemId = rawId;
        return this.lastName = name ?? string.Empty;
    }

    private void Prepare(AddonEvent _, AddonArgs args)
    {
        var addon = (AddonItemDetail*)args.Addon.Address;
        if (this.owner != null && this.owner != addon) this.Release(addon);
        this.RestoreName();
        if (this.english != null) this.english->ToggleVisibility(false);
        if (!this.config.ShowEnglishItemNames)
        {
            this.failed = false;
            return;
        }
        if (this.failed || this.disposed || !this.config.ServeLanguagePack) return;
        try
        {
            var name = addon->ItemNameText;
            var agent = AgentItemDetail.Instance();
            if (name == null || agent == null || agent->ItemId == 0
                || name->ParentNode == null || !name->IsVisible()) return;
            var id = agent->ItemId;
            if (!addon->DisplayingItemName)
            {
                if (ItemUtil.GetBaseId(id).ItemId != this.hoveredBaseId || this.hoveredGlamourId == 0) return;
                id = this.hoveredGlamourId;
            }
            var text = this.OriginalName(id);
            var displayed = SeString.Parse(name->NodeText.AsSpan()).TextValue;
            displayed = string.Concat(displayed.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.PrivateUse)).Trim();
            if (text.Length == 0 || string.Equals(displayed, text, StringComparison.OrdinalIgnoreCase)) return;
            if (this.english == null) this.Attach(addon, name);

            var before = name->NodeText.AsSpan();
            this.originalText = new byte[before.Length + 1];
            before.CopyTo(this.originalText);
            this.originalFont = name->FontSize;
            this.originalAlignment = name->AlignmentFontType;
            this.originalFlags = name->TextFlags;
            this.originalHeight = name->Height;
            this.originalY = name->Y;
            this.modifiedName = name;

            name->FontSize = 13;
            name->SetAlignment(AlignmentType.TopLeft);
            name->TextFlags = TextFlags.Edge | TextFlags.Ellipsis;
            name->SetHeight(20);
            name->SetYFloat(this.originalY + 6);
            name->SetText(addon->DisplayingItemName ? addon->ItemName.AsSpan() : addon->GlamourItemName.AsSpan());
            name->ApplyTextFlow();
            this.renderedText = name->NodeText.AsSpan().ToArray();

            this.english->FontSize = 11;
            this.english->SetAlignment(AlignmentType.TopLeft);
            this.english->SetWidth(name->Width);
            this.english->SetHeight(18);
            this.english->SetPositionFloat(name->X, this.originalY + 18);
            var label = addon->SpiritbondLabel;
            this.english->TextColor = label != null ? label->TextColor : name->TextColor;
            this.english->EdgeColor = label != null ? label->EdgeColor : name->EdgeColor;
            this.english->Color = name->Color;
            this.english->MultiplyRed = name->MultiplyRed;
            this.english->MultiplyGreen = name->MultiplyGreen;
            this.english->MultiplyBlue = name->MultiplyBlue;
            this.english->AddRed = name->AddRed;
            this.english->AddGreen = name->AddGreen;
            this.english->AddBlue = name->AddBlue;
            this.english->SetText(text);
            this.english->ApplyTextFlow();
            this.english->ToggleVisibility(true);
        }
        catch (Exception error)
        {
            this.RestoreName();
            if (this.english != null) this.english->ToggleVisibility(false);
            this.failed = true;
            this.log.Error(error, "Could not show the original item name.");
        }
    }

    private void Attach(AddonItemDetail* addon, AtkTextNode* name)
    {
        RemoveNameNodes(addon);
        var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
        if (node == null) throw new InvalidOperationException("Could not create the original item name node.");
        this.owner = addon;
        this.english = node;
        node->NodeId = NodeId;
        node->Type = NodeType.Text;
        node->NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft;
        node->TextFlags = TextFlags.Edge | TextFlags.Ellipsis;
        node->LineSpacing = name->LineSpacing;
        node->CharSpacing = name->CharSpacing;
        node->Color = name->Color;
        node->SetScale(name->ScaleX, name->ScaleY);
        node->ParentNode = name->ParentNode;
        node->PrevSiblingNode = name->PrevSiblingNode;
        node->NextSiblingNode = (AtkResNode*)name;
        if (name->PrevSiblingNode != null) name->PrevSiblingNode->NextSiblingNode = (AtkResNode*)node;
        name->PrevSiblingNode = (AtkResNode*)node;
        addon->UldManager.UpdateDrawNodeList();
    }

    // Keep the style through rendering. Restore it before the next game update.
    private void Restore(AddonEvent _, AddonArgs args) => this.RestoreName();

    private void RestoreName()
    {
        var name = this.modifiedName;
        if (name == null) return;
        this.modifiedName = null;
        name->FontSize = this.originalFont;
        name->AlignmentFontType = this.originalAlignment;
        name->TextFlags = this.originalFlags;
        name->SetHeight(this.originalHeight);
        name->SetYFloat(this.originalY);
        // Do not replace text that the game has already updated for another item.
        if (name->NodeText.AsSpan().SequenceEqual(this.renderedText))
            name->SetText(this.originalText.AsSpan());
        this.originalText = [];
        this.renderedText = [];
    }

    private void Finalize(AddonEvent _, AddonArgs args)
    {
        if ((AddonItemDetail*)args.Addon.Address == this.owner) this.Release(this.owner);
    }

    private void Release(AddonItemDetail* live)
    {
        var owner = this.owner;
        this.owner = null;
        this.english = null;
        if (owner == null || live != owner)
        {
            this.modifiedName = null;
            return;
        }
        this.RestoreName();
        RemoveNameNodes(live);
    }

    private static void RemoveNameNodes(AddonItemDetail* addon)
    {
        var name = addon->ItemNameText;
        if (name == null || name->ParentNode == null) return;
        var removed = new List<nint>();
        // Custom draw nodes must be found in the live sibling chain.
        for (var node = name->ParentNode->ChildNode; node != null;)
        {
            var previous = node->PrevSiblingNode;
            if (node->NodeId == NodeId && node->Type == NodeType.Text)
            {
                if (node->PrevSiblingNode != null) node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
                if (node->NextSiblingNode != null) node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
                if (node->ParentNode->ChildNode == node)
                    node->ParentNode->ChildNode = previous != null ? previous : node->NextSiblingNode;
                removed.Add((nint)node);
            }
            node = previous;
        }
        if (removed.Count == 0) return;
        addon->UldManager.UpdateDrawNodeList();
        foreach (var address in removed)
        {
            var node = (AtkTextNode*)address;
            node->AtkResNode.Destroy(false);
            IMemorySpace.Free(node, (ulong)sizeof(AtkTextNode));
        }
    }
}
