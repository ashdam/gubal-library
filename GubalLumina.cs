using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data;
using Lumina.Misc;

namespace GubalLibrary;

internal sealed record ShadowState(bool Ok, string Message, string Folder);

internal sealed class GubalLumina
{
    private const byte ExcelCategory = 0x0a;

    private const string IndexName = "0a0000.win32.index";
    private const string Dat1Name = "0a0000.win32.dat1";

    private const string Index2Name = "0a0000.win32.index2";

    private const int IndexHeaderAt = 0x400;

    private const int BlockData = 16000;

    private const uint Uncompressed = 32000;

    private const string StampName = "built-from.txt";

    public static string Folder(string configDirectory) => Path.Combine(configDirectory, "sheets");

    public static ShadowState Install(IDataManager data, IReadOnlyDictionary<string, string> pages, string folder, IPluginLog log)
    {
        try
        {
            var gameData = data.GameData;
            var source = Path.Combine(gameData.DataPath.FullName, "ffxiv", IndexName);
            if (!File.Exists(source))
            {
                return new ShadowState(false, $"the game's Excel index is not at {source}.", folder);
            }

            var stamp = Stamp(pages);
            var stampPath = Path.Combine(folder, StampName);
            var built = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;

            if (built != stamp)
            {
                Diagnostics.Log(log, $"[shadow] building for {stamp} (was {built ?? "nothing"})");
                var placed = Build(source, folder, pages, log);
                File.WriteAllText(stampPath, stamp);
                Diagnostics.Log(log, $"[shadow] {placed:N0} page(s) placed");
            }

            return Point(gameData, folder) is { } reason
                ? new ShadowState(false, reason, folder)
                : new ShadowState(true, "serving the pack to other plugins", folder);
        }
        catch (Exception e)
        {
            log.Error($"[shadow] not installed: {e}");
            return new ShadowState(false, e.Message, folder);
        }
    }

    // Include every selected path and its metadata so changes to parts rebuild the archive.
    private static string Stamp(IReadOnlyDictionary<string, string> pages)
    {
        var identity = new StringBuilder();
        foreach (var (gamePath, file) in pages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var fi = new FileInfo(file);
            identity.Append(gamePath).Append('\0').Append(fi.FullName).Append('\0')
                .Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        return $"{ExdRedirector.RunningGameVersion() ?? "unknown"}:{hash}";
    }

    private static int Build(string source, string folder, IReadOnlyDictionary<string, string> pages, IPluginLog log)
    {
        Directory.CreateDirectory(folder);
        Clear(folder, log);

        var index = Path.Combine(folder, IndexName);
        File.Copy(source, index, overwrite: true);

        var index2 = Path.Combine(Path.GetDirectoryName(source)!, Index2Name);
        if (File.Exists(index2))
        {
            File.Copy(index2, Path.Combine(folder, Index2Name), overwrite: true);
        }

        var dat1 = Path.Combine(folder, Dat1Name);
        var bytes = File.ReadAllBytes(index);
        var entriesAt = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(IndexHeaderAt + 8));
        var entriesSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(IndexHeaderAt + 12));

        var entryOf = new Dictionary<ulong, int>((int)(entriesSize / 16));
        for (var i = 0; i < entriesSize; i += 16)
        {
            var at = (int)entriesAt + i;
            entryOf[BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at))] = at;
        }

        var placed = 0;
        var skipped = 0;
        var end = 0L;

        // Write only, and a buffer worth having: this is one sequential append of about 100 MB.
        using (var dat = new FileStream(dat1, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            foreach (var (path, file) in pages)
            {
                var gamePath = path.ToLowerInvariant();
                var slash = gamePath.LastIndexOf('/');
                if (slash < 0)
                {
                    skipped++;
                    continue;
                }

                var hash = ((ulong)Crc32.Get(gamePath[..slash]) << 32) | Crc32.Get(gamePath[(slash + 1)..]);
                if (!entryOf.TryGetValue(hash, out var entryAt))
                {
                    skipped++;
                    continue;
                }

                var at = Append(dat, File.ReadAllBytes(file), ref end);
                if (at / 8 > 0xFFFFFFF0)
                {
                    throw new InvalidOperationException("the Excel archive outgrew what the index can address");
                }

                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(entryAt + 8),
                    ((uint)(at / 8) & 0xFFFFFFF0) + 2u);   // .dat1
                placed++;
            }
        }

        // Without this the second archive does not exist as far as Lumina is concerned.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(IndexHeaderAt + 80), 2);
        File.WriteAllBytes(index, bytes);

        if (skipped > 0)
        {
            Diagnostics.Log(log, $"[shadow] {skipped:N0} page(s) had no entry in this installation's index");
        }

        return placed;
    }

    private static void Clear(string folder, IPluginLog log)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e)
            {
                log.Warning($"[shadow] {Path.GetFileName(file)} could not be removed: {e.Message}");
            }
        }

        foreach (var stale in Directory.EnumerateDirectories(folder))
        {
            try
            {
                var count = Directory.EnumerateFiles(stale, "*", SearchOption.AllDirectories).Count();
                Directory.Delete(stale, recursive: true);
                removed += count;
            }
            catch (Exception e)
            {
                log.Warning($"[shadow] {Path.GetFileName(stale)} could not be removed: {e.Message}");
            }
        }

        if (removed > 0)
        {
            Diagnostics.Log(log, $"[shadow] emptied {removed:N0} file(s) from {folder}");
        }
    }

    private static long Append(FileStream dat, byte[] payload, ref long end)
    {
        var blocks = (payload.Length + BlockData - 1) / BlockData;
        var tableSize = blocks * 8;
        var headerSize = Align(24 + tableSize, 128);

        var at = Align(end, 128);
        if (at > end)
        {
            dat.Write(new byte[at - end]);
        }

        var entry = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), 2);                     // Standard
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(20), (uint)blocks);
        dat.Write(entry);

        var info = new byte[8];
        var blockAt = 0;
        for (var i = 0; i < blocks; i++)
        {
            var take = Math.Min(BlockData, payload.Length - (i * BlockData));
            var padded = Align(16 + take, 128);

            BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)blockAt);
            BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(4), (ushort)padded);
            BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(6), (ushort)take);
            dat.Write(info);
            blockAt += padded;
        }

        dat.Write(new byte[headerSize - 24 - tableSize]);

        var head = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(head, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), Uncompressed);
        for (var i = 0; i < blocks; i++)
        {
            var take = Math.Min(BlockData, payload.Length - (i * BlockData));
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)take);
            dat.Write(head);
            dat.Write(payload, i * BlockData, take);
            dat.Write(new byte[Align(16 + take, 128) - 16 - take]);
        }

        end = at + headerSize + blockAt;
        return at;
    }

    private static string? Point(GameData gameData, string folder)
    {
        var assembly = typeof(GameData).Assembly;
        var indexType = assembly.GetType("Lumina.Data.SqPackIndex");
        var categoryType = assembly.GetType("Lumina.Data.Category");
        if (indexType is null || categoryType is null)
        {
            return "Lumina has no SqPackIndex or Category any more";
        }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var indexCtor = indexType.GetConstructors(Any).FirstOrDefault(c =>
            c.GetParameters() is [{ ParameterType: var f }, { ParameterType: var g }]
            && f == typeof(FileInfo) && g == typeof(GameData));
        if (indexCtor is null)
        {
            return "Lumina has no SqPackIndex(FileInfo, GameData) any more";
        }

        var categoryCtor = categoryType.GetConstructor(Any, null,
            [typeof(byte), typeof(int), typeof(int), typeof(Lumina.Data.Structs.PlatformId),
                indexType, typeof(DirectoryInfo), typeof(GameData)], null);
        if (categoryCtor is null)
        {
            return "Lumina has no supported Category constructor";
        }

        var excel = typeof(GameData).GetProperty("Excel", BindingFlags.Instance | BindingFlags.Public);
        var setExcel = excel?.GetSetMethod(nonPublic: true);
        if (setExcel is null)
        {
            return "GameData.Excel can no longer be assigned";
        }

        if (!gameData.Repositories.TryGetValue("ffxiv", out var repository))
        {
            return $"no ffxiv repository to read; there are {string.Join(", ", gameData.Repositories.Keys)}";
        }

        if (!repository.Categories.TryGetValue(ExcelCategory, out var chunks) || chunks.Count == 0)
        {
            return "the ffxiv repository has no Excel category";
        }

        var live = chunks[0];
        var index = indexCtor.Invoke([new FileInfo(Path.Combine(folder, IndexName)), gameData]);

        // Keep the game category metadata when replacing its index and folder.
        var swapped = (Category)categoryCtor.Invoke(
        [
            live.CategoryId,
            live.Expansion,
            live.Chunk,
            live.Platform,
            index,
            new DirectoryInfo(folder),
            gameData,
        ]);

        // Borrow the original archive for pages outside the pack.
        if (!swapped.DatFiles.ContainsKey(0))
        {
            if (!live.DatFiles.TryGetValue(0, out var original))
            {
                return "the game's Excel archive is not open to borrow";
            }

            swapped.DatFiles[0] = original;
        }

        var previousExcel = gameData.Excel;
        try
        {
            // ExcelModule reads the replacement category during construction.
            repository.Categories[ExcelCategory] = [swapped];
            var replacementExcel = new Lumina.Excel.ExcelModule(gameData);
            setExcel.Invoke(gameData, [replacementExcel]);
        }
        catch
        {
            repository.Categories[ExcelCategory] = chunks;
            if (!ReferenceEquals(gameData.Excel, previousExcel))
                setExcel.Invoke(gameData, [previousExcel]);
            throw;
        }
        return null;
    }

    private static int Align(long value, int to) => (int)((value + to - 1) / to * to);
}
