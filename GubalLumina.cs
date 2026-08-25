using System.Buffers.Binary;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data;
using Lumina.Misc;

namespace GubalLibrary;

/// <summary>
///     Whether the pack is reaching Dalamud, and where from.
/// </summary>
/// <param name="Ok">Whether the pack is reaching the game and Dalamud. Never one without the other.</param>
/// <param name="Message">One sentence, shown to the player as it is.</param>
/// <param name="Folder">Where the Excel archive is, or would have gone.</param>
internal sealed record ShadowState(bool Ok, string Message, string Folder);

/// <summary>
///     Makes Dalamud's own Lumina read the installed pack, so a plugin asking a sheet what a row says
///     gets the same words the player is looking at.
/// </summary>
/// <remarks>
///     <para>
///         <b>The problem this exists for.</b> The read hook hands translated pages to the game. Lumina
///         opens the game's archives itself and never goes through it, so a plugin that reads a row and
///         compares it against what is on screen finds one language against another and matches nothing.
///         That is not a defect in the plugin: on a French client both sides come out of the same file
///         and agree. We are the only case in the ecosystem where they do not.
///     </para>
///     <para>
///         <b>ONE CATEGORY, NOT THE REPOSITORY.</b> Lumina's <c>ffxiv</c> repository holds fourteen
///         categories and Excel is one of them, <c>0a</c>. Only that category is replaced, with one
///         built over a folder holding two files: a copy of the Excel index with the pack's pages
///         repointed, and a <c>.dat1</c> holding those pages. The archive the untouched entries point
///         at is <b>borrowed</b> from the category being replaced, so the game's own 320 MB of Excel is
///         neither copied nor linked. The other thirteen categories are not touched at all, which is
///         why icons, models, fonts and sounds cannot be affected by any of this.
///     </para>
///     <para>
///         <b>Replacing the whole repository is what the first attempt did, and it does not work.</b>
///         A repository must be a folder holding all 59 of the category's files, which means linking
///         the game's own archives into it, which needs write permission on those archives. A game
///         installed where the official installer puts it, under <c>Program Files</c>, does not grant
///         that to an ordinary account: creating a hard link changes the source file's link count. It
///         also confined the folder to the game's own drive. Neither constraint survives here.
///     </para>
///     <para>
///         <b>Reflection, and why it is not as fragile as it looks.</b> Four members are reached that
///         way: the internal constructors of <c>SqPackIndex</c> and <c>Category</c>, the internal
///         setter on <c>GameData.Excel</c>, and nothing else. <c>Repository.Categories</c> and
///         <c>Category.DatFiles</c> both have public getters and hand back the live dictionaries.
///         <b>Every argument the Category constructor takes is read off the category being
///         replaced</b> rather than worked out here, so nothing depends on knowing what Lumina would
///         have computed. Every lookup is checked and reported by name, so a Lumina that changed says
///         so instead of half-working.
///     </para>
/// </remarks>
internal sealed class GubalLumina
{
    /// <summary>Excel is category 0a, and the only one this touches.</summary>
    private const byte ExcelCategory = 0x0a;

    private const string IndexName = "0a0000.win32.index";
    private const string Dat1Name = "0a0000.win32.dat1";

    /// <summary>Copied beside the index, and never repointed.</summary>
    /// <remarks>
    ///     A path is resolved against the index first, so an unmodified index2 cannot answer for a page
    ///     we placed: measured, with it present, and the sheet still came back translated. What it does
    ///     buy is parity with the category being replaced for anything that reads index2 directly, at
    ///     298 KB.
    /// </remarks>
    private const string Index2Name = "0a0000.win32.index2";

    /// <summary>The SqPack header occupies the first 0x400; the index header follows it.</summary>
    private const int IndexHeaderAt = 0x400;

    /// <summary>What the game puts in one block. Ours are the same size, just not compressed.</summary>
    private const int BlockData = 16000;

    /// <summary><c>DatBlockType.Uncompressed</c>. Writing blocks this way is what lets this exist
    /// without a deflate implementation, at the cost of a larger <c>.dat1</c>.</summary>
    private const uint Uncompressed = 32000;

    /// <summary>Records which pack and which patch the archive was built from.</summary>
    private const string StampName = "built-from.txt";

    /// <summary>Where the Excel archive is assembled.</summary>
    /// <remarks>
    ///     Beside the plugin's own files, always. Nothing is linked any more, so there is no volume to
    ///     match and no reason to ask anybody where to put it.
    /// </remarks>
    public static string Folder(string configDirectory) => Path.Combine(configDirectory, "sheets");

    /// <summary>
    ///     Builds the Excel archive if it is missing or stale, then points Lumina's Excel category at
    ///     it.
    /// </summary>
    /// <remarks>
    ///     <b>Never throws.</b> The caller is a plugin constructor, and the settings window has to
    ///     survive a failure here so somebody can read what went wrong.
    /// </remarks>
    public static ShadowState Install(IDataManager data, string packFolder, string folder, IPluginLog log)
    {
        try
        {
            if (!Directory.Exists(packFolder))
            {
                return new ShadowState(false, $"there is no language pack at {packFolder}.", folder);
            }

            var gameData = data.GameData;
            var source = Path.Combine(gameData.DataPath.FullName, "ffxiv", IndexName);
            if (!File.Exists(source))
            {
                return new ShadowState(false, $"the game's Excel index is not at {source}.", folder);
            }

            var stamp = Stamp(packFolder);
            var stampPath = Path.Combine(folder, StampName);
            var built = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;

            if (built != stamp)
            {
                log.Information($"[shadow] building for {stamp} (was {built ?? "nothing"})");
                var placed = Build(source, folder, packFolder, log);
                File.WriteAllText(stampPath, stamp);
                log.Information($"[shadow] {placed:N0} page(s) placed");
            }

            return Point(gameData, folder) is { } reason
                ? new ShadowState(false, reason, folder)
                : new ShadowState(true, "serving the pack to other plugins", folder);
        }
        catch (Exception e)
        {
            // Loud and named. A half-installed redirection would show up as other plugins
            // misbehaving, which is the one failure nobody would trace back to here.
            log.Error($"[shadow] not installed: {e}");
            return new ShadowState(false, e.Message, folder);
        }
    }

    /// <summary>
    ///     What identifies a built archive, so an unchanged one is reused and a stale one is rebuilt.
    /// </summary>
    /// <remarks>
    ///     Newest write plus total size across the pack, which catches both a rebuilt page and a part
    ///     being switched off. <b>And the game's own version</b>, because every entry in the copied
    ///     index is an offset into an archive the patcher rewrites: an archive that survived a patch
    ///     would not serve stale text, it would serve nonsense, to every plugin in the process.
    /// </remarks>
    private static string Stamp(string packFolder)
    {
        var newest = 0L;
        var total = 0L;
        var count = 0;
        foreach (var f in Directory.EnumerateFiles(packFolder, "*.exd", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
            total += fi.Length;
            count++;
        }

        return $"{ExdRedirector.RunningGameVersion() ?? "unknown"}:{count}:{total}:{newest}";
    }

    /// <summary>
    ///     Copies the game's Excel index, appends the pack's pages to a <c>.dat1</c> of our own, and
    ///     repoints the copied index at them.
    /// </summary>
    /// <returns>How many pages were placed.</returns>
    private static int Build(string source, string folder, string packFolder, IPluginLog log)
    {
        Directory.CreateDirectory(folder);
        Clear(folder, log);

        // The two index files are all that is copied, 943 KB between them. The 320 MB archive they
        // point into is borrowed in Point rather than copied or linked.
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

        // Built once. Scanning 37,872 entries per page would be 190 million comparisons over a pack
        // this size.
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
            foreach (var file in Directory.EnumerateFiles(packFolder, "*.exd", SearchOption.AllDirectories))
            {
                var gamePath = Path.GetRelativePath(packFolder, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .ToLowerInvariant();
                var slash = gamePath.LastIndexOf('/');
                if (slash < 0)
                {
                    skipped++;
                    continue;
                }

                var hash = ((ulong)Crc32.Get(gamePath[..slash]) << 32) | Crc32.Get(gamePath[(slash + 1)..]);
                if (!entryOf.TryGetValue(hash, out var entryAt))
                {
                    // A page for a file this installation does not have. Expected after a patch
                    // removes something, and never fatal.
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
            log.Information($"[shadow] {skipped:N0} page(s) had no entry in this installation's index");
        }

        return placed;
    }

    /// <summary>
    ///     Empties the folder completely before anything is written into it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>EVERYTHING, INCLUDING THE NAMES THIS IS ABOUT TO WRITE, AND THAT IS THE POINT.</b> A
    ///         version of this plugin that replaced the whole repository left 59 files here, 58 of
    ///         them hard links to the game's own archives, and two of those share a name with a file
    ///         written below. Copying over one of those names would follow the link and write inside
    ///         the game's own sqpack. Deleting a hard link only removes that name; the game's file is
    ///         untouched, which is exactly what is wanted.
    ///     </para>
    ///     <para>
    ///         The other 56 have to go for a second reason: a link keeps the blocks behind it alive,
    ///         so leaving them means an uninstalled game frees nothing and a file browser reads this
    ///         folder as tens of gigabytes.
    ///     </para>
    ///     <para>
    ///         Safe to empty because this folder is the plugin's own and nothing else is ever put in
    ///         it. The stamp goes too, and is written again once the build has finished, which is what
    ///         makes an interrupted build rebuild rather than pass for done.
    ///     </para>
    /// </remarks>
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

        // AND THE SUBFOLDERS, which is where the older version's 59 files are. It assembled a whole
        // repository under `ffxiv/`; this writes three files here and would never look inside, so the
        // links would sit there for good holding the game's blocks alive.
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
            log.Information($"[shadow] emptied {removed:N0} file(s) from {folder}");
        }
    }

    /// <summary>Writes one Standard file entry at <paramref name="end" /> and moves it past what was
    /// written.</summary>
    /// <remarks>
    ///     <b>The caller carries the offset.</b> Reading <see cref="FileStream.Length" /> flushes
    ///     whatever is buffered before it can answer, so asking per page costs a flush, a file-size
    ///     query and an extend, and the buffer never accumulates. The alignment gap is written rather
    ///     than seeked over for the same reason. The block table's offsets are arithmetic, so they are
    ///     computed rather than discovered by staging the body in memory and copying it out again.
    /// </remarks>
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

    /// <summary>
    ///     Replaces Lumina's Excel category with one over our folder and drops the sheet cache.
    /// </summary>
    /// <returns>Null when it worked, or what was missing.</returns>
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

        var categoryCtor = categoryType.GetConstructors(Any).FirstOrDefault(c => c.GetParameters().Length == 7);
        if (categoryCtor is null)
        {
            return "Lumina's Category constructor no longer takes the seven arguments this reads";
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

        // EVERY ARGUMENT BUT THE INDEX AND THE FOLDER COMES OFF THE CATEGORY BEING REPLACED. What a
        // chunk number or a platform id should be is Lumina's business, and reproducing its answer
        // here would be one more thing to get wrong on a version that changed it.
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

        // THE 320 MB THAT IS NEVER COPIED. Every entry we did not repoint still points into the
        // game's own .dat0, and the open handle for it is right there on the category being replaced.
        if (!swapped.DatFiles.ContainsKey(0))
        {
            if (!live.DatFiles.TryGetValue(0, out var original))
            {
                return "the game's Excel archive is not open to borrow";
            }

            swapped.DatFiles[0] = original;
        }

        repository.Categories[ExcelCategory] = [swapped];

        // The old module holds every sheet already read, so it has to go. Its constructor reads the
        // sheet list through the category above, which is also the first proof the archive is sound.
        setExcel.Invoke(gameData, [new Lumina.Excel.ExcelModule(gameData)]);
        return null;
    }

    private static int Align(long value, int to) => (int)((value + to - 1) / to * to);
}
