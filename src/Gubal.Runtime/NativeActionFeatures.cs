using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GubalLibrary;

namespace GubalStandalone;

internal sealed unsafe class NativeActionFeatures
{
    private readonly EnglishActionTooltip tooltip;
    private readonly StandaloneHooks hooks;
    private readonly RuntimeLog log;
    private INativeHook<AgentActionDetail.Delegates.Update>? update;
    private INativeHook<DrawAddon>? draw;
    private INativeHook<FinalizeAddon>? finalize;
    private AtkUnitBase* current;
    private bool lifecycleFailed;
    private delegate void DrawAddon(AtkUnitBase* addon);
    private delegate void FinalizeAddon(AtkUnitBase* addon);

    public NativeActionFeatures(ItemNames names, StandaloneHooks hooks, RuntimeLog log)
    {
        this.tooltip = new EnglishActionTooltip(names, _ => true, log);
        this.hooks = hooks;
        this.log = log;
        try
        {
            var table = AgentActionDetail.StaticVirtualTablePointer;
            if (table == null || table->Update == null)
                throw new InvalidOperationException("The action detail update function is unavailable.");
            this.update = hooks.Create<AgentActionDetail.Delegates.Update>((nint)table->Update, this.Update);
            this.update.Enable();
        }
        catch (Exception error)
        {
            this.update?.Dispose();
            log.Error(error, "Could not enable original action and trait names. Native tooltips remain active.");
        }
    }

    private void Update(AgentActionDetail* agent, uint frameCount)
    {
        // The update function can be shared by other agents.
        if (agent != AgentActionDetail.Instance())
        {
            this.update!.Original(agent, frameCount);
            return;
        }
        this.tooltip.Restore();
        this.update!.Original(agent, frameCount);
        if (this.lifecycleFailed) return;
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            var addon = manager == null ? null : manager->GetAddonByName("ActionDetail", 1);
            if (addon == null) return;
            if (this.draw == null)
            {
                var table = addon->VirtualTable;
                if (table == null || table->Draw == null || table->Finalizer == null)
                    throw new InvalidOperationException("The action tooltip lifecycle is unavailable.");
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
            this.log.Error(error, "Could not attach original action and trait names.");
        }
    }

    private void Draw(AtkUnitBase* addon)
    {
        if (addon != this.current)
        {
            this.draw!.Original(addon);
            return;
        }
        this.tooltip.Prepare(addon);
        try { this.draw!.Original(addon); }
        finally { this.tooltip.Restore(); }
    }

    private void Finalize(AtkUnitBase* addon)
    {
        if (addon == this.current)
        {
            this.tooltip.Release(addon);
            this.current = null;
        }
        this.finalize!.Original(addon);
    }
}
