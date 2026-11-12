using System.Buffers.Binary;
using GubalLibrary;
using Lumina.Data.Files.Excel;
using Lumina.Text.ReadOnly;

namespace GubalStandalone;

internal sealed class PackItemNames(ItemNames names, IReadOnlyDictionary<string, string> pages)
{
    private Dictionary<uint, string>? localized;

    public string Get(uint id)
    {
        this.localized ??= this.Load();
        return this.localized.GetValueOrDefault(id) ?? names.OriginalName(id);
    }

    private Dictionary<uint, string> Load()
    {
        // Item.Name uses fixed-data offset 12 in the typed Item schema.
        const int nameOffset = 12;
        var game = names.Original;
        _ = game.GetExcelSheet<Lumina.Excel.Sheets.Item>(Lumina.Data.Language.English)
            ?? throw new InvalidDataException("The original Item sheet is unavailable.");
        var header = game.GetFile<ExcelHeaderFile>("exd/item.exh")
            ?? throw new InvalidDataException("The Item header is unavailable.");
        if (!header.ColumnDefinitions.Any(column => column.Offset == nameOffset
            && column.Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String))
            throw new InvalidDataException("The Item name column is not compatible.");
        var result = new Dictionary<uint, string>();
        foreach (var (path, local) in pages)
        {
            if (!path.StartsWith("exd/item_", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith("_en.exd", StringComparison.OrdinalIgnoreCase)) continue;
            var page = game.GetFileFromDisk<ExcelDataFile>(local);
            foreach (var id in page.RowData.Keys)
            {
                var row = page.GetSpanForRow(id);
                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(row));
                row = row.Slice(6, size);
                var offset = checked(header.Header.DataOffset
                    + (int)BinaryPrimitives.ReadUInt32BigEndian(row.Slice(nameOffset, 4)));
                var text = row[offset..];
                var end = text.IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException("The Item name has no terminator.");
                result.Add(id, new ReadOnlySeStringSpan(text[..end]).ExtractText());
            }
        }
        return result;
    }
}
