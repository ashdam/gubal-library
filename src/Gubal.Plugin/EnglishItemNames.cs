using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GubalLibrary;

internal sealed unsafe class EnglishItemNames : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly ItemNames names;
    private readonly EnglishItemTooltip tooltip;
    private readonly Hook<AgentItemDetail.Delegates.OnItemHovered>? hoverHook;
    private bool disposed;

    public EnglishItemNames(Configuration config, IDataManager data, IGameGui gameGui,
        IAddonLifecycle lifecycle, IFramework framework, IGameInteropProvider interop,
        IContextMenu contextMenu, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.contextMenu = contextMenu;
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.log = log;
        this.names = new ItemNames(data.GameData.DataPath.FullName);
        this.tooltip = new EnglishItemTooltip(this.names, () => config.ShowEnglishItemNames,
            () => config.ServeLanguagePack, new DalamudGameLog(log));
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
        this.tooltip.Release((AddonItemDetail*)this.gameGui.GetAddonByName("ItemDetail").Address);
        this.names.Dispose();
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
        this.tooltip.RecordHover(result && outItem != null ? *outItem : null);
        return result;
    }

    internal string OriginalName(uint rawId) => this.names.OriginalName(rawId);

    private void Prepare(AddonEvent _, AddonArgs args) => this.tooltip.Prepare((AddonItemDetail*)args.Addon.Address);
    private void Restore(AddonEvent _, AddonArgs args) => this.tooltip.Restore();
    private void Finalize(AddonEvent _, AddonArgs args) => this.tooltip.Release((AddonItemDetail*)args.Addon.Address);
}
