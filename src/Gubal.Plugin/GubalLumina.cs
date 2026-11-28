using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data;
using Lumina.Misc;

namespace GubalLibrary;

internal sealed record ShadowState(bool Ok, string Message, string Folder, bool CacheLocked = false)
{
    public bool ClientStillServing { get; init; }

    public string? RestartNotice => !this.CacheLocked ? null : this.ClientStillServing
        ? CheapLoc.Loc.Localize("Pack.CacheLockedWhileServing",
            "Restart FFXIV so other plugins use the current translation.")
        : CheapLoc.Loc.Localize("Pack.CacheLocked",
            "The translation cache is in use. Close FFXIV completely and start it again to restore the translation.");
}

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

    public static void Prepare(string gameDataRoot, string folder, string workingPack, string installedPack,
        PackManifest manifest, ICollection<string> disabledSheets, IPluginLog log, CancellationToken cancel)
    {
        var version = ExdRedirector.RunningGameVersion();
        if (string.IsNullOrWhiteSpace(version) || !string.Equals(manifest.GameVersion, version, StringComparison.Ordinal))
            return;

        var source = Path.Combine(gameDataRoot, "ffxiv", IndexName);
        if (!File.Exists(source)) return;

        workingPack = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingPack));
        installedPack = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedPack));
        var pathLimit = ExdRedirector.MaxLocalPathLength + workingPack.Length - installedPack.Length;
        var pages = PackContents.Load(workingPack, pathLimit).Servable(disabledSheets);
        if (pages.Count == 0) return;

        var stamp = Stamp(pages, version, installedPack);
        var pending = folder + ".pending";
        if (ReadStamp(folder) == stamp || ReadStamp(pending) == stamp) return;

        var staging = folder + ".staging";
        try
        {
            BuildCache(source, staging, pages, stamp, log, cancel);
            cancel.ThrowIfCancellationRequested();
            MoveCache(staging, pending, log);
            Diagnostics.Log(log, "[shadow] prepared the archive for the next game start.");
        }
        finally
        {
            RemoveCache(staging, log);
        }
    }

    public static ShadowState Install(IDataManager data, IReadOnlyDictionary<string, string> pages, string folder,
        IPluginLog log, bool usePrepared)
    {
        try
        {
            var gameData = data.GameData;
            var source = Path.Combine(gameData.DataPath.FullName, "ffxiv", IndexName);
            if (!File.Exists(source))
            {
                return new ShadowState(false, $"the game's Excel index is not at {source}.", folder);
            }

            var stamp = Stamp(pages, ExdRedirector.RunningGameVersion());
            var built = ReadStamp(folder);
            var pending = folder + ".pending";
            if (built != stamp && usePrepared && ReadStamp(pending) == stamp)
            {
                FileOperationDiagnostics.Run("activate-prepared-cache", "sheet-cache", folder,
                    () => MoveCache(pending, folder, log));
                built = ReadStamp(folder);
                if (built == stamp) Diagnostics.Log(log, "[shadow] reusing the prepared archive.");
            }

            if (built != stamp)
            {
                Diagnostics.Log(log, $"[shadow] building for {stamp} (was {built ?? "nothing"})");
                BuildCache(source, folder, pages, stamp, log);
            }

            return FileOperationDiagnostics.Run("activate-cache", "sheet-cache", folder,
                () => Point(gameData, folder)) is { } reason
                ? new ShadowState(false, reason, folder)
                : new ShadowState(true, "serving the pack to other plugins", folder);
        }
        catch (Exception e)
        {
            log.Error(e, "[shadow] not installed.");
            var cacheLocked = e.GetBaseException() is IOException io
                && io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
            return new ShadowState(false, e.Message, folder, cacheLocked);
        }
    }

    // Include every selected path and its metadata so changes to parts rebuild the archive.
    private static string Stamp(IReadOnlyDictionary<string, string> pages, string? gameVersion, string? installedPack = null)
    {
        var identity = new StringBuilder();
        foreach (var (gamePath, file) in pages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var fi = new FileInfo(file);
            var installedPath = installedPack is null ? fi.FullName
                : Path.Combine(installedPack, gamePath.Replace('/', Path.DirectorySeparatorChar));
            identity.Append(gamePath).Append('\0').Append(installedPath).Append('\0')
                .Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        return $"{gameVersion ?? "unknown"}:{hash}";
    }

    private static string? ReadStamp(string folder)
    {
        var path = Path.Combine(folder, StampName);
        return File.Exists(path) && File.Exists(Path.Combine(folder, IndexName)) && File.Exists(Path.Combine(folder, Dat1Name))
            ? FileOperationDiagnostics.Run("read-cache-stamp", "sheet-cache", path, () => File.ReadAllText(path).Trim())
            : null;
    }

    private static void BuildCache(string source, string folder, IReadOnlyDictionary<string, string> pages, string stamp,
        IPluginLog log, CancellationToken cancel = default)
    {
        var placed = FileOperationDiagnostics.Run("build-cache", "sheet-cache", folder,
            () => Build(source, folder, pages, log, cancel));
        var path = Path.Combine(folder, StampName);
        FileOperationDiagnostics.Run("write-cache-stamp", "sheet-cache", path, () => File.WriteAllText(path, stamp));
        Diagnostics.Log(log, $"[shadow] {placed:N0} page(s) placed");
    }

    private static void MoveCache(string source, string destination, IPluginLog log)
    {
        var backup = destination + ".backup";
        if (Directory.Exists(destination))
        {
            RemoveCache(backup, log);
            Directory.Move(destination, backup);
        }

        try { Directory.Move(source, destination); }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }
        RemoveCache(backup, log);
    }

    private static void RemoveCache(string folder, IPluginLog log)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) { log.Warning(error, "Could not remove the unused shared translation archive."); }
    }

    private static int Build(string source, string folder, IReadOnlyDictionary<string, string> pages, IPluginLog log,
        CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        Clear(folder, log);

        var index = Path.Combine(folder, IndexName);
        FileOperationDiagnostics.Run("copy-cache-index", "sheet-cache", index,
            () => File.Copy(source, index, overwrite: true));

        var index2 = Path.Combine(Path.GetDirectoryName(source)!, Index2Name);
        if (File.Exists(index2))
        {
            var targetIndex2 = Path.Combine(folder, Index2Name);
            FileOperationDiagnostics.Run("copy-cache-index", "sheet-cache", targetIndex2,
                () => File.Copy(index2, targetIndex2, overwrite: true));
        }

        var dat1 = Path.Combine(folder, Dat1Name);
        var bytes = FileOperationDiagnostics.Run("read-cache-index", "sheet-cache", index,
            () => File.ReadAllBytes(index));
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
        try
        {
            using (var dat = FileOperationDiagnostics.Run("create-cache-data", "sheet-cache", dat1,
                () => new FileStream(dat1, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)))
            {
                foreach (var (path, file) in pages)
                {
                    cancel.ThrowIfCancellationRequested();
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

                    var payload = FileOperationDiagnostics.Run("read-pack-page", "pack", file,
                        () => File.ReadAllBytes(file));
                    var at = Append(dat, payload, ref end);
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

        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            FileOperationDiagnostics.Attach(error, "write-cache-data", "sheet-cache", dat1);
            throw;
        }

        // Without this the second archive does not exist as far as Lumina is concerned.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(IndexHeaderAt + 80), 2);
        FileOperationDiagnostics.Run("write-cache-index", "sheet-cache", index,
            () => File.WriteAllBytes(index, bytes));

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
