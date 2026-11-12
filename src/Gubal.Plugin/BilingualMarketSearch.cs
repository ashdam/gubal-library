using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace GubalLibrary;

internal sealed unsafe class BilingualMarketSearch : IDisposable
{
    // Spanish collation ignores accents but keeps n and ñ distinct.
    private static readonly CompareInfo SearchComparison = CultureInfo.GetCultureInfo("es-ES").CompareInfo;
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private const string UpdateSignature = "E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 0F 85";
    private const string PushSignature = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 89 9C 24 ?? ?? ?? ?? 41 2B C9";
    private delegate void SearchUpdate(AgentItemSearch* agent);
    private readonly IDataManager data;
    private readonly EnglishItemNames names;
    private readonly IPluginLog log;
    private Hook<SearchUpdate>? hook;
    private SearchUpdate? push;
    private Entry[] entries = [];

    public BilingualMarketSearch(IDataManager data, EnglishItemNames names, ISigScanner scanner,
        IGameInteropProvider interop, IPluginLog log)
    {
        this.data = data;
        this.names = names;
        this.log = log;
        try
        {
            var updateAddress = scanner.ScanText(UpdateSignature);
            var pushAddress = scanner.ScanText(PushSignature);
            this.push = Marshal.GetDelegateForFunctionPointer<SearchUpdate>(pushAddress);
            this.hook = interop.HookFromAddress<SearchUpdate>(updateAddress, this.Update);
            this.hook.Enable();
        }
        catch (Exception error)
        {
            this.hook?.Dispose();
            this.hook = null;
            this.log.Error(error, "Could not enable bilingual market search. Native search remains active.");
        }
    }

    private void Update(AgentItemSearch* agent)
    {
        if (agent == null || !agent->IsPartialSearching || agent->IsItemPushPending
            || agent->StringData == null || agent->ItemBuffer == null)
        {
            this.hook!.Original(agent);
            return;
        }

        uint[] matches;
        try
        {
            var input = agent->StringData->SearchParam.ToString();
            if (string.IsNullOrWhiteSpace(input))
            {
                this.hook!.Original(agent);
                return;
            }

            if (this.entries.Length == 0)
            {
                this.entries = this.data.GetExcelSheet<Item>()
                    .Where(item => item.ItemSearchCategory.RowId != 0)
                    .Select(item => new Entry(item.RowId, item.Name.ExtractText(), this.names.OriginalName(item.RowId)))
                    .ToArray();
            }

            matches = this.entries.Where(entry =>
                    SearchComparison.IndexOf(entry.Localized, input, SearchOptions) >= 0
                    || SearchComparison.IndexOf(entry.English, input, SearchOptions) >= 0)
                .Take(100).Select(entry => entry.Id).ToArray();
        }
        catch (Exception error)
        {
            this.log.Error(error, "Could not search both item names.");
            this.hook!.Original(agent);
            return;
        }

        // The client buffer accepts at most 100 item IDs.
        agent->ItemCount = 0;
        foreach (var id in matches) agent->ItemBuffer[agent->ItemCount++] = id;
        this.push!(agent);
    }

    public void Dispose() => this.hook?.Dispose();

    private sealed record Entry(uint Id, string Localized, string English);
}
