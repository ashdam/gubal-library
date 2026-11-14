using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Uld;

namespace GubalLibrary;

internal sealed unsafe class EnglishItemTooltip
{
    private const uint NodeId = 0x47554241;
    private uint hoveredBaseId;
    private uint hoveredGlamourId;
    private AddonItemDetail* owner;
    private AtkTextNode* english;
    private AtkTextNode* modifiedName;
    private byte[] originalText = [];
    private byte[] renderedText = [];
    private TextFlags originalFlags;
    private bool failed;
    private readonly ItemNameLayout? originalLayout;

    private readonly ItemNames names;
    private readonly Func<bool> enabled;
    private readonly Func<bool> translated;
    private readonly IPackInstallLog log;

    public EnglishItemTooltip(ItemNames names, Func<bool> enabled, Func<bool> translated, IPackInstallLog log)
    {
        this.names = names;
        this.enabled = enabled;
        this.translated = translated;
        this.log = log;
        try
        {
            var uld = names.Original.GetFile<UldFile>("ui/uld/ItemDetail.uld")
                ?? throw new InvalidDataException("The original ItemDetail layout is unavailable.");
            var node = uld.WidgetData.Nodes.First(node => node.NodeId == 33);
            if (node.Node is not NodeData.TextNode text)
                throw new InvalidDataException("The original item name node is not a text node.");
            this.originalLayout = new ItemNameLayout(node.Y, node.H, text.FontSize,
                (AlignmentType)text.Alignment);
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not load the original item name layout.");
        }
    }

    public void RecordHover(InventoryItem* item)
    {
        this.hoveredBaseId = item == null ? 0 : ItemNames.BaseId(item->GetItemId());
        this.hoveredGlamourId = item == null ? 0 : item->GetGlamourId();
    }

    public void Prepare(AddonItemDetail* addon)
    {
        if (this.owner != null && this.owner != addon) this.Release(addon);
        this.RestoreText();
        if (this.english != null) this.english->ToggleVisibility(false);
        if (!this.enabled())
        {
            this.RestoreName();
            this.failed = false;
            return;
        }
        if (this.failed || this.originalLayout == null || !this.translated())
        {
            this.RestoreName();
            return;
        }
        try
        {
            var name = addon->ItemNameText;
            var agent = AgentItemDetail.Instance();
            if (name == null || agent == null || agent->ItemId == 0
                || name->ParentNode == null || !name->IsVisible())
            {
                this.RestoreName();
                return;
            }
            if (this.modifiedName != null && this.modifiedName != name) this.RestoreName();
            var id = agent->ItemId;
            if (!addon->DisplayingItemName)
            {
                if (ItemNames.BaseId(id) != this.hoveredBaseId || this.hoveredGlamourId == 0)
                {
                    this.RestoreName();
                    return;
                }
                id = this.hoveredGlamourId;
            }
            var text = this.names.OriginalName(id);
            var displayed = new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(name->NodeText.AsSpan()).ExtractText();
            displayed = string.Concat(displayed.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.PrivateUse)).Trim();
            if (text.Length == 0 || string.Equals(displayed, text, StringComparison.OrdinalIgnoreCase))
            {
                this.RestoreName();
                return;
            }
            if (this.english == null) this.Attach(addon, name);

            var before = name->NodeText.AsSpan();
            this.originalText = new byte[before.Length + 1];
            before.CopyTo(this.originalText);
            this.originalFlags = name->TextFlags;
            this.modifiedName = name;

            name->FontSize = 13;
            name->SetAlignment(AlignmentType.Left);
            name->TextFlags = TextFlags.Edge | TextFlags.Ellipsis;
            name->SetHeight(20);
            name->SetYFloat(22);
            name->SetText(addon->DisplayingItemName ? addon->ItemName.AsSpan() : addon->GlamourItemName.AsSpan());
            name->ApplyTextFlow();
            this.renderedText = name->NodeText.AsSpan().ToArray();

            this.english->FontSize = 11;
            this.english->SetAlignment(AlignmentType.Left);
            this.english->SetWidth(name->Width);
            this.english->SetHeight(18);
            this.english->SetPositionFloat(name->X, 42);
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

    public void Restore()
    {
        // Keep the active geometry while the game updates node transforms.
        if (this.enabled() && this.translated()) this.RestoreText();
        else
        {
            this.RestoreName();
            if (this.english != null) this.english->ToggleVisibility(false);
        }
    }

    private void RestoreName()
    {
        var name = this.modifiedName;
        if (name == null) return;
        this.RestoreText();
        var layout = this.originalLayout!.Value;
        name->FontSize = layout.FontSize;
        name->SetAlignment(layout.Alignment);
        name->SetHeight(layout.Height);
        name->SetYFloat(layout.Y);
        this.modifiedName = null;
    }

    private void RestoreText()
    {
        var name = this.modifiedName;
        if (name == null || this.originalText.Length == 0) return;
        name->TextFlags = this.originalFlags;
        // Do not replace text that the game has already updated for another item.
        if (name->NodeText.AsSpan().SequenceEqual(this.renderedText))
            name->SetText(this.originalText.AsSpan());
        this.originalText = [];
        this.renderedText = [];
    }

    public void Release(AddonItemDetail* live)
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

    private readonly record struct ItemNameLayout(float Y, ushort Height, byte FontSize, AlignmentType Alignment);

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
