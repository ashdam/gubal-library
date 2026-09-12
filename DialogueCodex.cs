using CheapLoc;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.Payloads;

namespace GubalLibrary;

internal sealed unsafe class DialogueCodex : IDisposable
{
    private const string BuildLinksSignature = "48 85 C9 0F 84 ?? ?? ?? ?? 48 89 6C 24 ?? 57 48 83 EC ?? 48 89 5C 24 ?? 0F B6 FA 48 8B E9 84 D2";
    private readonly delegate* unmanaged<AtkTextNode*, byte, void> buildLinks;
    private const string OpenEntrySignature = "48 89 5C 24 ?? 57 48 83 EC ?? C6 41 ?? 03 48 8B D9 8B 02 89 41 ?? 48 8B 01 FF 50 ??";
    private readonly delegate* unmanaged<AgentInterface*, uint*, void> openEntry;
    private readonly Configuration config;
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly IClientState client;
    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IUiBuilder uiBuilder;
    private bool cursorRequested;
    private bool ownsCursor;
    private bool previousCursorOverride;
    private AtkCursor.CursorType previousCursor;
    private readonly List<(CodexTerm Term, uint Quest)> catalog = [];
    private readonly HashSet<uint> available = [];
    private CodexTerm[] currentTerms = [];
    private CodexTerms terms = new([]);
    private AtkTextNode* node;
    private bool addedLinkFlag;
    private bool managedLinks;
    private bool active;
    private bool failed;
    private nint openedCodex;
    private uint? pendingEntry;
    private DateTime openDeadline;
    private bool ownsPendingWindow;
    private bool openedInCutscene;
    private byte[] original = [];
    private byte[] decorated = [];
    private DateTime nextCatalogRefresh;

    public string Status { get; private set; } = string.Empty;

    public void DrawLinks()
    {
        this.cursorRequested = false;
        try
        {
            this.DrawLinkRegions();
        }
        finally
        {
            this.SetNativeCursor(this.cursorRequested && !this.failed);
        }
    }

    private void DrawLinkRegions()
    {
        var addon = (AtkUnitBase*)this.gameGui.GetAddonByName("Talk").Address;
        if (!this.config.EnableCodexLinks || this.failed || this.node == null)
            return;

        if (addon == null || !addon->IsVisible || addon->GetTextNodeById(3) != this.node)
            return;
        if (this.node->LinkData == null)
            return;

        try
        {
            var scale = new Vector2(this.node->GetScaleX(), this.node->GetScaleY());
            var origin = ImGui.GetMainViewport().Pos + new Vector2(this.node->ScreenX, this.node->ScreenY);
            LinkData* selected = null;
            var index = 0;
            foreach (var item in *this.node->LinkData)
            {
                var link = item.Value;
                if (link == null || link->LinkType != (byte)LinkMacroPayloadType.AkatsukiNote
                    || !this.available.Contains(link->UIntValue1))
                    continue;
                var size = new Vector2(link->MaxX - link->MinX, link->MaxY - link->MinY) * scale;
                if (size.X <= 0 || size.Y <= 0)
                    continue;

                ImGui.SetNextWindowPos(origin + new Vector2(link->MinX, link->MinY) * scale);
                ImGui.SetNextWindowSize(size);
                using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                using var minimum = ImRaii.PushStyle(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
                var visible = ImGui.Begin($"###GubalCodexLink{index++}",
                    ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
                try
                {
                    if (!visible)
                        continue;
                    if (ImGui.InvisibleButton("entry", size))
                        selected = link;
                    if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    {
                        this.cursorRequested = true;
                        ImGui.SetNextFrameWantCaptureMouse(true);
                    }
                }
                finally
                {
                    ImGui.End();
                }
            }

            if (selected != null)
                this.OpenLink(selected);
        }
        catch (Exception exception)
        {
            this.Fail(exception);
        }
    }

    public DialogueCodex(Configuration config, IDataManager data, IGameGui gameGui,
        IClientState client, IAddonLifecycle lifecycle,
        IFramework framework, IPluginLog log, ISigScanner scanner, IUiBuilder uiBuilder)
    {
        this.config = config;
        this.data = data;
        this.gameGui = gameGui;
        this.client = client;
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.log = log;
        this.uiBuilder = uiBuilder;
        uiBuilder.HideUi += this.ReleaseCursor;
        if (scanner.TryScanText(BuildLinksSignature, out var address))
            this.buildLinks = (delegate* unmanaged<AtkTextNode*, byte, void>)address;
        if (scanner.TryScanText(OpenEntrySignature, out address))
            this.openEntry = (delegate* unmanaged<AgentInterface*, uint*, void>)address;
        lifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", this.OnTalkUpdate);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", this.OnFinalize);
        framework.Update += this.Update;
    }

    public void Dispose()
    {
        this.uiBuilder.HideUi -= this.ReleaseCursor;
        this.framework.Update -= this.Update;
        this.lifecycle.UnregisterListener(this.OnTalkUpdate, this.OnFinalize);
        this.Detach();
        this.CloseCodex();
    }

    public void PrintExample(IChatGui chat)
    {
        this.Update(this.framework);
        if (!this.active || this.failed || this.available.Count == 0)
            return;

        var sample = string.Join(" / ", this.currentTerms.Select(term => term.Name).Distinct().Take(2));
        var bytes = this.terms.Decorate(System.Text.Encoding.UTF8.GetBytes(sample), out _);
        chat.Print(SeString.Parse(bytes));
    }

    private void Update(IFramework _)
    {
        var enabled = this.config.EnableCodexLinks && this.client.IsLoggedIn;
        if (!enabled)
        {
            if (this.active)
            {
                this.Detach();
                this.CloseCodex();
            }
            this.active = false;
            this.failed = false;
            this.catalog.Clear();
            this.available.Clear();
            this.currentTerms = [];
            this.Status = this.config.EnableCodexLinks
                ? Loc.Localize("Codex.Login", "Log in to use the Unending Codex.") : string.Empty;
            return;
        }

        try
        {
            if (!this.active)
            {
                this.active = true;
                this.ReadCatalog();
                this.nextCatalogRefresh = DateTime.MinValue;
            }

            if (this.openedInCutscene && !this.uiBuilder.CutsceneActive)
            {
                this.CloseCodex();
                this.ReleaseCursor();
            }

            if (this.openedCodex != 0)
            {
                var window = (AtkUnitBase*)this.gameGui.GetAddonByName("AkatsukiNote").Address;
                if (window == null || (nint)window != this.openedCodex)
                {
                    this.openedCodex = 0;
                    this.openedInCutscene = false;
                }
            }

            if (this.pendingEntry is { } requested)
            {
                var window = (AtkUnitBase*)this.gameGui.GetAddonByName("AkatsukiNote").Address;
                if (window != null && window->IsVisible)
                {
                    Diagnostics.Log(this.log, "[codex] Codex window is visible after request for entry {Entry}.", requested);
                    if (this.ownsPendingWindow)
                        this.openedCodex = (nint)window;
                    this.pendingEntry = null;
                }
                else if (DateTime.UtcNow >= this.openDeadline)
                {
                    Diagnostics.Log(this.log, "[codex] Codex window did not appear for entry {Entry}.", requested);
                    this.pendingEntry = null;
                }
            }

            if (this.failed || DateTime.UtcNow < this.nextCatalogRefresh)
                return;

            this.nextCatalogRefresh = DateTime.UtcNow.AddSeconds(1);
            var unlocked = this.catalog.Where(item => item.Quest != 0 && QuestManager.IsQuestComplete(item.Quest))
                .Select(item => item.Term).Distinct().ToArray();
            var ids = unlocked.Select(term => term.Id).ToHashSet();
            if (!this.currentTerms.SequenceEqual(unlocked))
            {
                this.Detach();
                this.available.Clear();
                this.available.UnionWith(ids);
                this.currentTerms = unlocked;
                this.terms = new CodexTerms(unlocked);
            }

            this.Status = ids.Count == 0
                ? Loc.Localize("Codex.None", "No unlocked Codex entries are available.")
                : Loc.Localize("Codex.Ready", "Ready. Open a dialogue that names a Codex entry.");
        }
        catch (Exception exception)
        {
            this.Fail(exception);
        }
    }

    private void ReadCatalog()
    {
        if (this.buildLinks == null)
            throw new InvalidOperationException("The native text link builder is unavailable.");
        if (this.openEntry == null)
            throw new InvalidOperationException("The native Codex entry opener is unavailable.");
        this.catalog.Clear();
        foreach (var group in this.data.GetSubrowExcelSheet<AkatsukiNote>())
        {
            foreach (var row in group)
            {
                var name = row.ListName.Value.Text.ExtractText().Trim();
                if (name.Length > 1)
                    this.catalog.Add((new CodexTerm(row.RowId, name), row.UnlockOnQuest.RowId));
            }
        }

        // A name must identify one entry. Different entries with the same name need context.
        var ambiguous = this.catalog.GroupBy(item => item.Term.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Term.Id).Distinct().Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        this.catalog.RemoveAll(item => ambiguous.Contains(item.Term.Name));
    }

    private void OnTalkUpdate(AddonEvent _, AddonArgs args)
    {
        if (!this.active || this.failed || !this.config.EnableCodexLinks || this.available.Count == 0)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (!addon->IsVisible)
            {
                this.Detach();
                return;
            }

            var text = addon->GetTextNodeById(3);
            if (text == null)
                return;
            if (this.node != text)
            {
                this.Detach();
                this.node = text;
            }

            var current = text->NodeText.AsSpan();
            if (current.SequenceEqual(this.decorated) || current.SequenceEqual(this.original))
            {
                if (this.decorated.Length > 0 && !current.SequenceEqual(this.decorated))
                {
                    this.SetText(this.decorated);
                    if (this.managedLinks)
                        this.buildLinks(text, 1);
                }
                return;
            }

            this.original = current.ToArray();
            this.decorated = this.terms.Decorate(current, out var matches);
            if (matches == 0)
            {
                var unchanged = this.original;
                this.Detach();
                this.node = text;
                this.original = unchanged;
                this.decorated = unchanged;
                return;
            }

            if (!this.managedLinks)
            {
                this.addedLinkFlag = !text->TextFlags.HasFlag(TextFlags.LinkData);
                this.managedLinks = true;
            }

            this.SetText(this.decorated);
            // The native builder allocates, clears, and fills the node's link list.
            this.buildLinks(text, 1);
        }
        catch (Exception exception)
        {
            this.Fail(exception);
        }
    }

    private void OpenLink(LinkData* link)
    {
        try
        {
            var entry = link->UIntValue1;
            var module = AgentModule.Instance();
            var agent = module == null ? null : module->GetAgentByInternalId(AgentId.AkatsukiNote);
            if (agent == null || this.openEntry == null)
                throw new InvalidOperationException("The native Codex entry opener is unavailable.");

            var before = (AtkUnitBase*)this.gameGui.GetAddonByName("AkatsukiNote").Address;
            this.ownsPendingWindow = before == null || !before->IsVisible;
            if (this.ownsPendingWindow)
                this.openedInCutscene = this.uiBuilder.CutsceneActive;
            this.pendingEntry = entry;
            this.openDeadline = DateTime.UtcNow.AddSeconds(2);
            Diagnostics.Log(this.log, "[codex] Requesting entry {Entry} through the Codex agent.", entry);
            this.openEntry(agent, &entry);
            if (agent->IsAgentActive() && agent->IsAddonHidden())
            {
                agent->ShowAddon();
            }
        }
        catch (Exception exception)
        {
            this.Fail(exception);
        }
    }

    private void OnFinalize(AddonEvent _, AddonArgs args) => this.Detach();

    private void SetText(byte[] bytes)
    {
        // SetText requires a null terminator and retains the supplied pointer.
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        this.node->SetText(terminated.AsSpan());
    }

    private void Detach()
    {
        this.ReleaseCursor();
        if (this.node != null)
        {
            if (this.node->NodeText.AsSpan().SequenceEqual(this.decorated) && this.original.Length > 0)
                this.SetText(this.original);
            if (this.managedLinks)
                this.buildLinks(this.node, this.addedLinkFlag ? (byte)0 : (byte)1);
        }

        this.node = null;
        this.original = [];
        this.decorated = [];
        this.addedLinkFlag = false;
        this.managedLinks = false;
    }

    private void ReleaseCursor() => this.SetNativeCursor(false);

    private void SetNativeCursor(bool enabled)
    {
        var stage = AtkStage.Instance();
        if (enabled && stage != null)
        {
            if (!this.ownsCursor)
            {
                this.previousCursorOverride = this.uiBuilder.OverrideGameCursor;
                this.previousCursor = stage->AtkCursor.Type;
                this.ownsCursor = true;
            }
            // Dalamud must allow the native cursor while the link captures the mouse.
            this.uiBuilder.OverrideGameCursor = false;
            stage->AtkCursor.SetCursorType(AtkCursor.CursorType.Clickable, true);
        }
        else if (this.ownsCursor)
        {
            if (stage != null && stage->AtkCursor.Type == AtkCursor.CursorType.Clickable)
                stage->AtkCursor.SetCursorType(this.previousCursor, true);
            this.uiBuilder.OverrideGameCursor = this.previousCursorOverride;
            this.ownsCursor = false;
        }
    }

    private void CloseCodex()
    {
        this.pendingEntry = null;
        var window = (AtkUnitBase*)this.gameGui.GetAddonByName("AkatsukiNote").Address;
        if (this.openedCodex != 0 && (nint)window == this.openedCodex)
        {
            var module = AgentModule.Instance();
            var agent = module == null ? null : module->GetAgentByInternalId(AgentId.AkatsukiNote);
            // Close through the agent so its state matches the window state.
            if (agent != null)
                agent->Hide();
        }
        this.openedCodex = 0;
        this.openedInCutscene = false;
    }

    private void Fail(Exception exception)
    {
        this.failed = true;
        this.Detach();
        this.Status = Loc.Localize("Codex.Failed", "Codex links are unavailable. See /xllog for details.");
        this.log.Error(exception, "Could not apply dialogue Codex links.");
    }
}
