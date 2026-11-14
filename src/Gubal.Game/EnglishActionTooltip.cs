using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GubalLibrary;

internal sealed unsafe class EnglishActionTooltip(ItemNames names, Func<DetailKind, bool> enabled, IPackInstallLog log)
{
    private const uint NodeId = 0x47554242;
    private static readonly uint[] DetailNodes = [6, 8, 9, 11, 12];
    private AtkUnitBase* owner;
    private AtkTextNode* english;
    private float subtitleY = 29;
    private float traitSubtitleY = 30;
    private bool displayingTrait;
    private ushort? subtitleWidth;
    private ushort? subtitleHeight;
    private byte subtitleFontSize = 10;
    private readonly List<(nint Node, byte FontSize, byte Alignment)> styles = [];
    private bool failed;

    public void Prepare(AtkUnitBase* addon)
    {
        if (this.owner != null && this.owner != addon) this.Release(addon);
        this.Restore();
        if (this.english != null) this.english->ToggleVisibility(false);
        if (this.failed) return;
        try
        {
            var agent = AgentActionDetail.Instance();
            if (agent == null || agent->ActionId == 0
                || agent->ActionKind is not (DetailKind.Action or DetailKind.CraftingAction or DetailKind.Trait)
                || !enabled(agent->ActionKind)) return;
            var name = addon->GetNodeById(5);
            var category = addon->GetNodeById(6);
            if (name == null || name->Type != NodeType.Text || !name->IsVisible()
                || category == null || category->Type != NodeType.Text) return;
            var text = agent->ActionKind == DetailKind.Trait ? names.OriginalTraitName(agent->ActionId)
                : names.OriginalActionName(agent->ActionId, agent->ActionKind == DetailKind.CraftingAction);
            var title = (AtkTextNode*)name;
            var displayed = new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(title->NodeText.AsSpan()).ExtractText();
            if (text.Length == 0 || string.Equals(displayed.Trim(), text, StringComparison.OrdinalIgnoreCase)) return;
            this.displayingTrait = agent->ActionKind == DetailKind.Trait;
            if (this.english == null) this.Attach(addon, title);

            this.SetStyle(title, 14, AlignmentType.TopLeft);
            foreach (var id in DetailNodes)
            {
                var node = addon->GetNodeById(id);
                if (node == null || node->Type != NodeType.Text) continue;
                this.SetStyle((AtkTextNode*)node, 11,
                    id is 8 or 11 ? AlignmentType.BottomRight : AlignmentType.BottomLeft);
            }

            var subtitle = this.english;
            subtitle->SetYFloat(this.displayingTrait ? this.traitSubtitleY : this.subtitleY);
            subtitle->FontSize = this.subtitleFontSize;
            subtitle->SetWidth(this.subtitleWidth!.Value);
            subtitle->SetHeight(this.subtitleHeight!.Value);
            subtitle->TextColor = ((AtkTextNode*)category)->TextColor;
            subtitle->EdgeColor = title->EdgeColor;
            subtitle->SetText(text);
            subtitle->ApplyTextFlow();
            subtitle->ToggleVisibility(true);
        }
        catch (Exception error)
        {
            this.Restore();
            if (this.english != null) this.english->ToggleVisibility(false);
            this.failed = true;
            log.Error(error, "Could not show the original action name.");
        }
    }

    public void Restore()
    {
        if (this.english != null)
        {
            if (this.displayingTrait) this.traitSubtitleY = this.english->Y;
            else this.subtitleY = this.english->Y;
            this.subtitleWidth = this.english->Width;
            this.subtitleHeight = this.english->Height;
            this.subtitleFontSize = this.english->FontSize;
        }
        foreach (var (address, fontSize, alignment) in this.styles)
        {
            var node = (AtkTextNode*)address;
            node->FontSize = fontSize;
            node->AlignmentFontType = alignment;
            node->SetAlignment((AlignmentType)(alignment & 0x0f));
            RefreshTextStyle(node);
        }
        this.styles.Clear();
    }

    private void SetStyle(AtkTextNode* node, byte fontSize, AlignmentType alignment)
    {
        this.styles.Add(((nint)node, node->FontSize, node->AlignmentFontType));
        node->FontSize = fontSize;
        node->SetAlignment(alignment);
        RefreshTextStyle(node);
    }

    private static void RefreshTextStyle(AtkTextNode* node)
    {
        // Leave the game-owned text and its source pointer unchanged.
        if ((node->TextFlags & TextFlags.FontCache) != 0)
        {
            node->FreeFontCache();
            node->BuildFontCache();
        }
        node->IsDirty = true;
    }

    private void Attach(AtkUnitBase* addon, AtkTextNode* title)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
        if (node == null) throw new InvalidOperationException("Could not create the original action name node.");
        this.owner = addon;
        this.english = node;
        node->NodeId = NodeId;
        node->Type = NodeType.Text;
        node->NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft;
        node->TextFlags = TextFlags.Edge | TextFlags.Ellipsis;
        node->Color = title->Color;
        node->SetScale(title->ScaleX, title->ScaleY);
        this.subtitleWidth ??= title->Width;
        this.subtitleHeight ??= 18;
        node->SetPositionFloat(title->X, this.displayingTrait ? this.traitSubtitleY : this.subtitleY);
        node->SetWidth(this.subtitleWidth.Value);
        node->SetHeight(this.subtitleHeight.Value);
        node->FontSize = this.subtitleFontSize;
        node->SetAlignment(AlignmentType.TopLeft);
        node->ParentNode = title->ParentNode;
        node->PrevSiblingNode = title->PrevSiblingNode;
        node->NextSiblingNode = (AtkResNode*)title;
        if (title->PrevSiblingNode != null) title->PrevSiblingNode->NextSiblingNode = (AtkResNode*)node;
        title->PrevSiblingNode = (AtkResNode*)node;
        addon->UldManager.UpdateDrawNodeList();
    }

    public void Release(AtkUnitBase* live)
    {
        if (this.owner == live && live != null)
        {
            this.Restore();
            var node = this.english;
            if (node != null)
            {
                if (node->PrevSiblingNode != null) node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
                if (node->NextSiblingNode != null) node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
                if (node->ParentNode->ChildNode == (AtkResNode*)node)
                    node->ParentNode->ChildNode = node->PrevSiblingNode != null ? node->PrevSiblingNode : node->NextSiblingNode;
                live->UldManager.UpdateDrawNodeList();
                node->AtkResNode.Destroy(false);
                IMemorySpace.Free(node, (ulong)sizeof(AtkTextNode));
            }
        }
        this.owner = null;
        this.english = null;
        this.styles.Clear();
    }
}
