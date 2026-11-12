using System.Globalization;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GubalLibrary;

internal sealed unsafe class BilingualMarketSearch : IDisposable
{
    // Spanish collation ignores accents but keeps n and Ã± distinct.
    private static readonly CompareInfo SearchComparison = CultureInfo.GetCultureInfo("es-ES").CompareInfo;
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private const string UpdateSignature = "E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 0F 85";
    private const string PushSignature = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 89 9C 24 ?? ?? ?? ?? 41 2B C9";
    private delegate void SearchUpdate(AgentItemSearch* agent);
    private readonly Func<IEnumerable<MarketItem>> items;
    private readonly IPackInstallLog log;
    private INativeHook<SearchUpdate>? hook;
    private SearchUpdate? push;
    private MarketItem[] entries = [];

    public BilingualMarketSearch(Func<IEnumerable<MarketItem>> items, INativeSignatures scanner,
        INativeHooks interop, IPackInstallLog log)
    {
        this.items = items;
        this.log = log;
        try
        {
            if (!scanner.TryScanText(UpdateSignature, out var updateAddress)
                || !scanner.TryScanText(PushSignature, out var pushAddress))
                throw new InvalidOperationException("Market search signatures were not found.");
            this.push = Marshal.GetDelegateForFunctionPointer<SearchUpdate>(pushAddress);
            this.hook = interop.Create<SearchUpdate>(updateAddress, this.Update);
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
                this.entries = this.items().ToArray();
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


}

internal sealed record MarketItem(uint Id, string Localized, string English);
