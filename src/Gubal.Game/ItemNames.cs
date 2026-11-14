using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using System.Collections.Frozen;
using System.Diagnostics;

namespace GubalLibrary;

internal sealed class ItemNames(string sqpack) : IDisposable
{
    private GameData? original;
    private readonly Dictionary<uint, string> names = [];
    private readonly Dictionary<(uint Id, bool Crafting), string> actionNames = [];
    private readonly Dictionary<uint, string> traitNames = [];
    private readonly CancellationTokenSource preloadCancellation = new();
    private Task<OriginalTooltipNames>? preload;
    internal GameData Original => this.original ??= new GameData(sqpack);
    internal bool IsReady => this.preload == null || this.preload.IsCompletedSuccessfully;

    public static uint BaseId(uint id) => id >= 2_000_000 ? id
        : id >= 1_000_000 ? id - 1_000_000 : id >= 500_000 ? id - 500_000 : id;

    public string OriginalName(uint rawId)
    {
        var id = BaseId(rawId);
        if (this.preload is { } task)
            return task.IsCompletedSuccessfully ? task.Result.Items.GetValueOrDefault(id, string.Empty) : string.Empty;
        if (this.names.TryGetValue(id, out var cached)) return cached;
        var name = id >= 2_000_000
            ? this.Original.GetExcelSheet<EventItem>(Language.English)?.GetRowOrDefault(id)?.Name.ExtractText()
            : this.Original.GetExcelSheet<Item>(Language.English)?.GetRowOrDefault(id)?.Name.ExtractText();
        return this.names[id] = name ?? string.Empty;
    }

    public IEnumerable<MarketItem> MarketItems(Func<uint, string> localized)
    {
        var sheet = this.Original.GetExcelSheet<Item>(Language.English)
            ?? throw new InvalidDataException("The original Item sheet is unavailable.");
        foreach (var item in sheet)
            if (item.ItemSearchCategory.RowId != 0)
                yield return new MarketItem(item.RowId, localized(item.RowId), this.OriginalName(item.RowId));
    }

    public string OriginalActionName(uint id, bool crafting)
    {
        if (this.preload is { } task)
            return task.IsCompletedSuccessfully
                ? (crafting ? task.Result.CraftActions : task.Result.Actions).GetValueOrDefault(id, string.Empty)
                : string.Empty;
        if (this.actionNames.TryGetValue((id, crafting), out var cached)) return cached;
        var name = crafting
            ? this.Original.GetExcelSheet<CraftAction>(Language.English)?.GetRowOrDefault(id)?.Name.ExtractText()
            : this.Original.GetExcelSheet<Lumina.Excel.Sheets.Action>(Language.English)?.GetRowOrDefault(id)?.Name.ExtractText();
        return this.actionNames[(id, crafting)] = name ?? string.Empty;
    }

    public string OriginalTraitName(uint id)
    {
        if (this.preload is { } task)
            return task.IsCompletedSuccessfully ? task.Result.Traits.GetValueOrDefault(id, string.Empty) : string.Empty;
        if (this.traitNames.TryGetValue(id, out var cached)) return cached;
        return this.traitNames[id] = this.Original.GetExcelSheet<Trait>(Language.English)
            ?.GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
    }

    public void Preload(IPackInstallLog log)
    {
        if (this.preload != null) return;
        var token = this.preloadCancellation.Token;
        this.preload = Task.Run(() =>
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                using var game = new GameData(sqpack);
                token.ThrowIfCancellationRequested();
                var items = new Dictionary<uint, string>();
                foreach (var row in game.GetExcelSheet<Item>(Language.English)
                    ?? throw new InvalidDataException("The original Item sheet is unavailable."))
                {
                    token.ThrowIfCancellationRequested();
                    items[row.RowId] = row.Name.ExtractText();
                }
                foreach (var row in game.GetExcelSheet<EventItem>(Language.English)
                    ?? throw new InvalidDataException("The original EventItem sheet is unavailable."))
                {
                    token.ThrowIfCancellationRequested();
                    items[row.RowId] = row.Name.ExtractText();
                }
                var actions = new Dictionary<uint, string>();
                foreach (var row in game.GetExcelSheet<Lumina.Excel.Sheets.Action>(Language.English)
                    ?? throw new InvalidDataException("The original Action sheet is unavailable."))
                {
                    token.ThrowIfCancellationRequested();
                    actions[row.RowId] = row.Name.ExtractText();
                }
                var craftActions = new Dictionary<uint, string>();
                foreach (var row in game.GetExcelSheet<CraftAction>(Language.English)
                    ?? throw new InvalidDataException("The original CraftAction sheet is unavailable."))
                {
                    token.ThrowIfCancellationRequested();
                    craftActions[row.RowId] = row.Name.ExtractText();
                }
                var traits = new Dictionary<uint, string>();
                foreach (var row in game.GetExcelSheet<Trait>(Language.English)
                    ?? throw new InvalidDataException("The original Trait sheet is unavailable."))
                {
                    token.ThrowIfCancellationRequested();
                    traits[row.RowId] = row.Name.ExtractText();
                }
                var result = new OriginalTooltipNames(items.ToFrozenDictionary(), actions.ToFrozenDictionary(),
                    craftActions.ToFrozenDictionary(), traits.ToFrozenDictionary());
                token.ThrowIfCancellationRequested();
                log.Information("Original tooltip names loaded in {ElapsedMilliseconds} ms.", elapsed.ElapsedMilliseconds);
                return result;
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                log.Warning(error, "Could not load original tooltip names.");
                return new OriginalTooltipNames(FrozenDictionary<uint, string>.Empty,
                    FrozenDictionary<uint, string>.Empty, FrozenDictionary<uint, string>.Empty,
                    FrozenDictionary<uint, string>.Empty);
            }
        }, token);
    }

    public void Dispose()
    {
        this.preloadCancellation.Cancel();
        this.preloadCancellation.Dispose();
        this.original?.Dispose();
    }

    private sealed record OriginalTooltipNames(FrozenDictionary<uint, string> Items,
        FrozenDictionary<uint, string> Actions, FrozenDictionary<uint, string> CraftActions,
        FrozenDictionary<uint, string> Traits);
}
