using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GubalLibrary;

namespace GubalStandalone;

internal sealed unsafe class NativeItemFeatures
{
    private readonly ItemNames names;
    private readonly EnglishItemTooltip tooltip;
    private readonly BilingualMarketSearch market;
    private readonly StandaloneHooks hooks = new();
    private readonly RuntimeLog log;
    private INativeHook<AgentItemDetail.Delegates.OnItemHovered>? hover;
    private INativeHook<AddonItemDetail.Delegates.GenerateItemTooltip>? generate;
    private INativeHook<DrawAddon>? draw;
    private INativeHook<FinalizeAddon>? finalize;
    private AddonItemDetail* current;
    private bool lifecycleFailed;
    private delegate void DrawAddon(AtkUnitBase* addon);
    private delegate void FinalizeAddon(AtkUnitBase* addon);

    public NativeItemFeatures(string sqpack, IReadOnlyDictionary<string, string> pages,
        ImageSignatures signatures, RuntimeLog log)
    {
        this.log = log;
        this.names = new ItemNames(sqpack);
        this.tooltip = new EnglishItemTooltip(this.names, () => true, () => true, log);
        var localized = new PackItemNames(this.names, pages);
        this.market = new BilingualMarketSearch(() => this.names.MarketItems(localized.Get), signatures, this.hooks, log);
        try
        {
            var hoverAddress = AgentItemDetail.Addresses.OnItemHovered.Value;
            var generateAddress = AddonItemDetail.Addresses.GenerateItemTooltip.Value;
            if (hoverAddress == 0 || generateAddress == 0)
                throw new InvalidOperationException("Item tooltip signatures were not found.");
            this.hover = this.hooks.Create<AgentItemDetail.Delegates.OnItemHovered>(hoverAddress, this.Hover);
            this.generate = this.hooks.Create<AddonItemDetail.Delegates.GenerateItemTooltip>(generateAddress, this.Generate);
            this.hover.Enable();
            this.generate.Enable();
        }
        catch (Exception error)
        {
            this.generate?.Dispose();
            this.hover?.Dispose();
            log.Error(error, "Could not enable original item subtitles. Native tooltips remain active.");
        }
    }

    private bool Hover(AgentItemDetail* agent, InventoryItem** outItem, InventoryType* container,
        short* slot, uint itemId, uint rowIdOrIndex, InventoryItem* item)
    {
        var result = this.hover!.Original(agent, outItem, container, slot, itemId, rowIdOrIndex, item);
        this.tooltip.RecordHover(result && outItem != null ? *outItem : null);
        return result;
    }

    private void Generate(AddonItemDetail* addon, NumberArrayData* numbers, StringArrayData* strings)
    {
        if (!this.lifecycleFailed && addon != null)
        {
            try
            {
                if (this.draw is null)
                {
                    var table = ((AtkUnitBase*)addon)->VirtualTable;
                    if (table == null || table->Draw == null || table->Finalizer == null)
                        throw new InvalidOperationException("The item tooltip lifecycle is unavailable.");
                    this.draw = this.hooks.Create<DrawAddon>((nint)table->Draw, this.Draw);
                    this.finalize = this.hooks.Create<FinalizeAddon>((nint)table->Finalizer, this.Finalize);
                    this.finalize.Enable();
                    this.draw.Enable();
                }
                this.current = addon;
            }
            catch (Exception error)
            {
                this.draw?.Dispose();
                this.finalize?.Dispose();
                this.lifecycleFailed = true;
                this.log.Error(error, "Could not attach original item subtitles.");
            }
        }
        this.generate!.Original(addon, numbers, strings);
    }

    private void Draw(AtkUnitBase* addon)
    {
        if ((nint)addon != (nint)this.current)
        {
            this.draw!.Original(addon);
            return;
        }
        this.tooltip.Prepare(this.current);
        try { this.draw!.Original(addon); }
        finally { this.tooltip.Restore(); }
    }

    private void Finalize(AtkUnitBase* addon)
    {
        if ((nint)addon == (nint)this.current)
        {
            this.tooltip.Release(this.current);
            this.current = null;
        }
        this.finalize!.Original(addon);
    }
}
