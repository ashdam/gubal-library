using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private byte originalFont;
    private byte originalAlignment;
    private ushort originalHeight;
    private float originalY;
    private TextFlags originalFlags;
    private bool failed;

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
    }

    public void RecordHover(InventoryItem* item)
    {
        this.hoveredBaseId = item == null ? 0 : ItemNames.BaseId(item->GetItemId());
        this.hoveredGlamourId = item == null ? 0 : item->GetGlamourId();
    }

    public void Prepare(AddonItemDetail* addon)
    {
        if (this.owner != null && this.owner != addon) this.Release(addon);
        this.RestoreName();
        if (this.english != null) this.english->ToggleVisibility(false);
        if (!this.enabled())
        {
            this.failed = false;
            return;
        }
        if (this.failed || !this.translated()) return;
        try
        {
            var name = addon->ItemNameText;
            var agent = AgentItemDetail.Instance();
            if (name == null || agent == null || agent->ItemId == 0
                || name->ParentNode == null || !name->IsVisible()) return;
            var id = agent->ItemId;
            if (!addon->DisplayingItemName)
            {
                if (ItemNames.BaseId(id) != this.hoveredBaseId || this.hoveredGlamourId == 0) return;
                id = this.hoveredGlamourId;
            }
            var text = this.names.OriginalName(id);
            var displayed = new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(name->NodeText.AsSpan()).ExtractText();
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
    public void Restore() => this.RestoreName();

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
