using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;

namespace GubalLibrary;

internal sealed class ItemNames(string sqpack) : IDisposable
{
    private GameData? original;
    private readonly Dictionary<uint, string> names = [];
    internal GameData Original => this.original ??= new GameData(sqpack);

    public static uint BaseId(uint id) => id >= 2_000_000 ? id
        : id >= 1_000_000 ? id - 1_000_000 : id >= 500_000 ? id - 500_000 : id;

    public string OriginalName(uint rawId)
    {
        var id = BaseId(rawId);
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

    public void Dispose() => this.original?.Dispose();
}
