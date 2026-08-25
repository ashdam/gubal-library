using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Misc;

namespace GubalLibrary;

/// <summary>
///     Whether the pack is reaching Dalamud, and where from.
/// </summary>
/// <remarks>
///     <b>The plugin always loads; the pack is what does not get served.</b> Those are different
///     things and the difference is the settings window: a plugin whose constructor throws never
///     becomes an instance, so there is nothing to open and the folder that caused the trouble cannot
///     be corrected from anywhere but a text editor. Loading anyway costs nothing — nothing is being
///     served, which is the part that matters — and leaves the repair one tab away.
/// </remarks>
/// <param name="Ok">Whether the pack is reaching the game and Dalamud. Never one without the other.</param>
/// <param name="Message">One sentence, shown to the player as it is.</param>
/// <param name="Folder">Where the archive is, or would have gone.</param>
internal sealed record ShadowState(bool Ok, string Message, string Folder);

/// <summary>
///     Makes Dalamud's own Lumina read the installed pack, so a plugin asking a sheet what a row says
///     gets the same words the player is looking at.
/// </summary>
/// <remarks>
///     <para>
///         <b>The problem this exists for.</b> The read hook hands translated pages to the game. Lumina
///         opens the game's archives itself and never goes through it, so a plugin that reads a row and
///         compares it against what is on screen finds English against Spanish and matches nothing.
///         That is not a defect in the plugin: on a French client both sides come out of the same file
///         and agree. We are the only case in the ecosystem where they do not.
///     </para>
///     <para>
///         <b>How.</b> A shadow copy of the Excel archive, in this plugin's own config directory: the
///         game's own archives linked rather than copied, our pages appended to a <c>.dat1</c> of
///         our own, and a copy of the one index we touch repointed at them. <b>Links or nothing</b>:
///         the repository is 63 GB, so a failure to link refuses rather than copies. Then Lumina's repository for
///         <c>ffxiv</c> is replaced with one pointed at that folder. <b>The game's own files are never
///         touched</b>, so no patch undoes this and nothing marks the installation as modified.
///     </para>
///     <para>
///         <b>On by default, and all or nothing.</b> It changes what EVERY loaded plugin reads. Nearly
///         all of them want the localised text — they read a row to compare against the screen or to
///         show it to the player — but a plugin that compares a row against English written into its
///         own source would stop matching. Measured before this was written: of the sheets the
///         installed plugins reference, this pack translates eight, and none of the ones they resolve
///         things by name with — <c>Item</c>, <c>PlaceName</c>, <c>TerritoryType</c>, <c>Action</c>,
///         <c>ClassJob</c> — are among them. When the archive cannot be assembled the pack is not
///         served either: a translation that reaches the screen but not Dalamud is the exact failure
///         this class exists to prevent, so half of it is worse than none.
///     </para>
///     <para>
///         <b>Run <c>Tools/ShadowValidator</c> after changing anything here.</b> It compares every
///         entry of every category through both repositories, over a million of them, and passes only
///         when the pack's own pages are the sole difference. Nothing else proves this is transparent
///         for the fourteen categories it replaces.
///     </para>
///     <para>
///         <b>What hard links cost, which is not disk.</b> The folder reports tens of gigabytes and
///         occupies about a hundred megabytes: the drive's free space is right, because NTFS counts
///         shared clusters once, and only tools that add up file sizes are fooled. Two consequences
///         are real and neither is worth avoiding. <b>Uninstalling the game frees nothing while this
///         folder still names its files</b>, because blocks live until the last name goes. And <b>a
///         backup that walks files and copies them takes the full amount</b> unless it understands
///         links, which robocopy does and several cloud sync clients do not.
///     </para>
///     <para>
///         <b>A rebuild costs about half a second, on the boot path, with Dalamud holding the game's
///         start.</b> That is cheap enough to leave where it is. The timing is logged in three parts
///         rather than as a total because one rebuild has been seen to take 24 seconds without ever
///         repeating: if it returns, the line says which phase. Were it ever to become normal, the
///         answer is to assemble the archive when the pack is installed instead of at startup.
///     </para>
///     <para>
///         <b>Reflection, and why it is not as fragile as it looks.</b> Three members are reached that
///         way: the internal <c>Repository(DirectoryInfo, GameData)</c> constructor, the internal
///         setter on <c>GameData.Excel</c>, and nothing else — the <c>Repositories</c> dictionary has a
///         public getter and hands back the live instance. All three sit on a public surface, and
///         Dalamud pins one Lumina version, so the shape only moves when Dalamud moves. Every lookup
///         is checked and reported by name, so a Lumina that changed says so instead of half-working.
///     </para>
/// </remarks>
internal sealed partial class GubalLumina
{
    /// <summary>Excel is category 0a. Every other category is linked through untouched — see Build.</summary>
    private const string IndexName = "0a0000.win32.index";
    private const string Dat1Name = "0a0000.win32.dat1";

    /// <summary>The SqPack header occupies the first 0x400; the index header follows it.</summary>
    private const int IndexHeaderAt = 0x400;

    /// <summary>What the game puts in one block. Ours are the same size, just not compressed.</summary>
    private const int BlockData = 16000;

    /// <summary><c>DatBlockType.Uncompressed</c>. Writing blocks this way is what lets this exist
    /// without a deflate implementation, at the cost of a larger <c>.dat1</c>.</summary>
    private const uint Uncompressed = 32000;

    /// <summary>Records which pack the shadow was built from, so a new pack rebuilds it and an
    /// unchanged one does not.</summary>
    private const string StampName = "built-from.txt";

    /// <summary>The file <see cref="CanServe" /> links and unlinks to find out whether it can.</summary>
    private const string ProbeName = "link-probe.tmp";

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string link, string existing, nint attributes);

    /// <summary>
    ///     Picks where the shadow archive goes: beside the plugin's own files when that works, and on
    ///     the game's drive when it has to be.
    /// </summary>
    /// <remarks>
    ///     A hard link is a second name for the same bytes, so it cannot leave the volume those bytes
    ///     are on. Dalamud's config directory is usually on the system drive and the game often is
    ///     not, and that is the whole reason this is not simply one fixed path. <b>Never inside the
    ///     game's own directory</b> — the same volume is all a link needs, and the install is not ours
    ///     to write in.
    /// </remarks>
    /// <param name="configured">What the player set, or empty to choose.</param>
    public static string ChooseFolder(string configured, string configDirectory, string sqpack)
    {
        if (configured is { Length: > 0 })
        {
            return configured;
        }

        var beside = Path.Combine(configDirectory, "sheets");
        var gameDrive = Path.GetPathRoot(sqpack);
        if (gameDrive is null || string.Equals(Path.GetPathRoot(beside), gameDrive, StringComparison.OrdinalIgnoreCase))
        {
            return beside;
        }

        // Same volume as the game, outside its directory. Named so somebody who finds it knows what
        // it is and that deleting it costs nothing but a rebuild.
        return Path.Combine(gameDrive, "GubalLibrary", "sheets");
    }

    /// <summary>
    ///     Answers, in microseconds and before anything has been served, whether this machine can
    ///     hold a shadow archive at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Asked first because of what the answer decides.</b> A pack that reaches the screen
    ///         but not Dalamud leaves every plugin comparing English against Spanish, which is the
    ///         failure this whole class exists to prevent — so when the archive cannot be assembled
    ///         the right thing is to serve nothing at all, not to serve half. Knowing that before the
    ///         read hook goes in is what makes it one restart instead of two.
    ///     </para>
    ///     <para>
    ///         <b>One link, then undone.</b> A hard link copies nothing, so the probe costs a file
    ///         handle. It is also the only honest test: whether two paths are on one volume can be
    ///         guessed from their roots, but a junction, a mapped drive or a mounted folder makes the
    ///         guess wrong, and how the system is configured changes the answer without changing
    ///         either path.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     Settles which folder to use, preferring what the player set and falling back to what this
    ///     plugin would have chosen.
    /// </summary>
    /// <remarks>
    ///     <b>The fallback exists to keep a typo from being permanent.</b> Failing to assemble the
    ///     archive stops the plugin loading, and a plugin that does not load has no settings window —
    ///     so a folder typed into Advanced that turns out not to work would lock the only place it
    ///     could be corrected. Trying the chosen default before giving up means the way back is a
    ///     restart rather than editing a configuration file by hand. The default is on the game's own
    ///     drive, which is the one thing the archive actually requires.
    /// </remarks>
    public static ShadowState Resolve(
        string configured, string configDirectory, string sqpack, string packFolder, IPluginLog log)
    {
        var wanted = ChooseFolder(configured, configDirectory, sqpack);
        var refused = CanServe(packFolder, wanted, sqpack, log);
        if (refused is null)
        {
            return new ShadowState(true, "ready", wanted);
        }

        var chosen = ChooseFolder(string.Empty, configDirectory, sqpack);
        if (!string.Equals(chosen, wanted, StringComparison.OrdinalIgnoreCase))
        {
            log.Warning($"[shadow] {wanted} will not do ({refused}), falling back to {chosen}");
            if (CanServe(packFolder, chosen, sqpack, log) is null)
            {
                return new ShadowState(true, "ready", chosen);
            }
        }

        return new ShadowState(false, refused, wanted);
    }

    /// <returns>Null when the archive can be built, or the sentence to show the player.</returns>
    public static string? CanServe(string packFolder, string shadowRoot, string sqpack, IPluginLog log)
    {
        if (!Directory.Exists(packFolder))
        {
            return $"there is no language pack at {packFolder}.";
        }

        var source = Path.Combine(sqpack, "ffxiv");
        var sample = Directory.Exists(source) ? Directory.EnumerateFiles(source).FirstOrDefault() : null;
        if (sample is null)
        {
            return $"the game's archives are not where this plugin expected them: {source}.";
        }

        var shadow = Path.Combine(shadowRoot, "ffxiv");
        var existed = Directory.Exists(shadow);
        try
        {
            Directory.CreateDirectory(shadow);
        }
        catch (Exception e)
        {
            return $"{shadow} cannot be created: {e.Message}";
        }

        var probe = Path.Combine(shadow, ProbeName);
        Erase(probe);
        var linked = Link(sample, probe, log);
        Erase(probe);

        if (linked)
        {
            return null;
        }

        // Tidy up after a refusal. Typing a folder that turns out not to work is a normal thing to
        // do — it is what the fallback exists for — and it should not leave a directory behind on
        // somebody's drive for them to find later and wonder about. Only what this call created, and
        // only while it is still empty.
        if (!existed)
        {
            TryRemoveEmpty(shadow);
            TryRemoveEmpty(shadowRoot);
        }

        return CannotLink(sample, probe);
    }

    /// <summary>Removes a directory only if it is ours to remove and holds nothing.</summary>
    private static void TryRemoveEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception)
        {
            // An empty folder left behind is not worth a word, let alone a failure.
        }
    }

    /// <summary>
    ///     Builds the shadow archive if it is missing or stale, then points Lumina at it.
    /// </summary>
    /// <remarks>
    ///     <b>Never throws.</b> The caller is a plugin constructor, and a failure here has to leave a
    ///     working settings window behind it — the folder that caused the trouble is edited there.
    /// </remarks>
    public static ShadowState Install(IDataManager data, string packFolder, string shadowRoot, IPluginLog log)
    {
        try
        {
            var gameData = data.GameData;
            var source = Path.Combine(gameData.DataPath.FullName, "ffxiv");
            var shadow = Path.Combine(shadowRoot, "ffxiv");

            var stamp = Stamp(packFolder);
            var stampPath = Path.Combine(shadow, StampName);
            var built = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;

            if (built != stamp)
            {
                log.Information($"[shadow] building for {stamp} (was {built ?? "nothing"})");
                var placed = Build(source, shadow, packFolder, log);
                File.WriteAllText(stampPath, stamp);
                log.Information($"[shadow] {placed:N0} page(s) placed");
            }

            return Point(gameData, shadow) is { } reason
                ? new ShadowState(false, reason, shadowRoot)
                : new ShadowState(true, "serving the pack to other plugins", shadowRoot);
        }
        catch (Exception e)
        {
            // Loud and named. A half-installed redirection would show up as other plugins
            // misbehaving, which is the one failure nobody would trace back to here.
            log.Error($"[shadow] not installed: {e}");
            return new ShadowState(false, e.Message, shadowRoot);
        }
    }

    /// <summary>
    ///     What identifies a built shadow, so an unchanged one is reused and a stale one is rebuilt.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Newest write plus total size across the pack: cheap, and it catches both a rebuilt
    ///         page and a part being switched off.
    ///     </para>
    ///     <para>
    ///         <b>And the game's own version, which is not decoration.</b> The index in the shadow is
    ///         a copy, and every entry in it is an offset into an archive the patcher rewrites. After
    ///         a patch those offsets point at whatever now occupies that address, so a shadow that
    ///         survives a patch does not serve stale Spanish — it serves nonsense, to every plugin in
    ///         the process, with nothing in any log. The pack alone cannot notice: it does not change
    ///         when the game does.
    ///     </para>
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
    ///     Assembles the shadow folder: index copied, <c>.dat0</c> hardlinked, our pages appended to
    ///     <c>.dat1</c>, index entries repointed at them.
    /// </summary>
    private static int Build(string source, string shadow, string packFolder, IPluginLog log)
    {
        Directory.CreateDirectory(shadow);

        // TIMED IN THREE PARTS, and not for tidiness. This runs inside the plugin's constructor while
        // Dalamud holds the game's boot, so what it costs is a black screen somebody is looking at —
        // and it is not paid once, because every pack update moves the stamp and rebuilds. The first
        // rebuild after a machine starts took 24 s where a warm one takes half of one, and guessing
        // which of the three parts that was is exactly the sort of thing this project does not do.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Clear(source, shadow, log);
        var cleared = clock.ElapsedMilliseconds;

        // EVERY file in the repository, not just Excel's. Replacing Lumina's `ffxiv` repository
        // replaces all fourteen categories at once, so a folder holding only 0a0000 makes icons,
        // textures and sounds vanish for every plugin that asks Lumina for one — which is exactly
        // what the first in-game run did, and it showed up as AutoRetainer failing to load job
        // icons. They are links, so the 63 GB behind them costs nothing.
        var index = Path.Combine(shadow, IndexName);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            var landing = Path.Combine(shadow, name);
            if (File.Exists(landing))
            {
                File.Delete(landing);
            }

            if (name.Equals(IndexName, StringComparison.OrdinalIgnoreCase))
            {
                // The one file we modify, so it has to be ours rather than the game's.
                File.Copy(file, landing, overwrite: true);
            }
            else if (!Link(file, landing, log))
            {
                // COPYING IS NOT AN OPTION: the repository is 63 GB, not the 320 MB Excel archive it
                // is easy to picture. Refusing leaves the player exactly as they were. Reaching here
                // at all means CanServe linked a file and this one would not, which is a disk filling
                // up rather than the ordinary two-drives case.
                throw new InvalidOperationException(CannotLink(file, landing));
            }
        }

        var linked = clock.ElapsedMilliseconds;

        var dat1 = Path.Combine(shadow, Dat1Name);
        if (File.Exists(dat1))
        {
            File.Delete(dat1);
        }

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
        // Write only, and a buffer worth having: this is one sequential append of about 100 MB.
        var end = 0L;
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
                    throw new InvalidOperationException("the shadow archive outgrew what the index can address");
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

        log.Information(
            $"[shadow] {cleared:N0} ms emptying, {linked - cleared:N0} ms linking, "
            + $"{clock.ElapsedMilliseconds - linked:N0} ms writing the pages");

        return placed;
    }

    /// <summary>
    ///     Empties the shadow folder before it is rebuilt, and touches nothing it does not recognise.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why emptying rather than overwriting.</b> A patch can replace an archive instead of
    ///         rewriting it, and a hard link to the replaced file keeps the old one alive — the bytes
    ///         stop being shared, and the 101 MB this costs quietly becomes the tens of gigabytes it
    ///         appears to. An archive the patch removed altogether would linger for good.
    ///     </para>
    ///     <para>
    ///         <b>Why by name rather than deleting the folder.</b> Where this folder goes is a
    ///         setting, and somebody will one day point it at a directory that already holds
    ///         something. So only what belongs to this feature is removed — the game's own archive
    ///         names, our <c>.dat1</c>, the stamp — and anything else is left where it is and said
    ///         out loud.
    ///     </para>
    /// </remarks>
    private static void Clear(string source, string shadow, IPluginLog log)
    {
        var known = Directory.EnumerateFiles(source)
            .Select(Path.GetFileName)
            .Concat([Dat1Name, StampName, ProbeName])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var strangers = 0;
        foreach (var file in Directory.EnumerateFiles(shadow))
        {
            if (known.Contains(Path.GetFileName(file)))
            {
                Erase(file);
            }
            else
            {
                strangers++;
            }
        }

        if (strangers > 0)
        {
            log.Warning($"[shadow] left {strangers:N0} file(s) in {shadow} that this plugin did not put there");
        }
    }

    /// <summary>Deletes a file we own, where failing to is not worth an exception.</summary>
    private static void Erase(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left behind rather than fatal: the caller either overwrites it or reports the link
            // failure that follows, and both say more than this would.
        }
    }

    /// <summary>What a player is told when the archives cannot be linked. The diagnosis only.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Written for somebody who did not build this.</b> An earlier version said "the game
    ///         is on C:\ and this folder on D:\" — which names neither the game nor the folder to
    ///         anybody who is not holding the source. So: FINAL FANTASY XIV by name, the folder by
    ///         its full path, and the word <em>drive</em> said out loud, because the whole problem is
    ///         that they are two.
    ///     </para>
    ///     <para>
    ///         <b>The requirement, not the mechanism.</b> Whether the files are linked, and why a
    ///         link cannot cross drives, explains how Windows behaves and helps nobody reading this.
    ///         What they need is the rule they have to satisfy: the two have to be on one drive.
    ///     </para>
    ///     <para>
    ///         <b>The diagnosis and nothing else.</b> What to do about it differs by where this is
    ///         read: chat can offer a link to the settings, and the settings cannot offer a link to
    ///         themselves. So the caller adds the instruction.
    ///     </para>
    /// </remarks>
    public static string CannotLink(string original, string link) =>
        $"FINAL FANTASY XIV is on drive {Path.GetPathRoot(original)?.TrimEnd('\\')} and the folder "
        + $"chosen for the translation is on drive {Path.GetPathRoot(link)?.TrimEnd('\\')} "
        + $"({Path.GetDirectoryName(Path.GetDirectoryName(link))}). They have to be on the same drive.";

    /// <summary>
    ///     Gives the game's archive a second name in the shadow folder, cheapest way first.
    /// </summary>
    /// <remarks>
    ///     Two kinds, because they fail in opposite situations. A <b>hard link</b> is a second name
    ///     for the same bytes, so it needs no privileges but cannot leave the volume the bytes are on
    ///     — and this folder sits in the user profile while the game may not. A <b>symbolic link</b>
    ///     points at a path rather than at bytes, so it crosses volumes happily, but Windows only
    ///     lets an ordinary account create one where the system is set up to allow it. <b>There is no
    ///     third tier:</b>
    ///     copying was one until the repository turned out to be 63 GB rather than the 320 MB Excel
    ///     archive that was in view when it was written.
    /// </remarks>
    private static bool Link(string original, string link, IPluginLog log)
    {
        if (CreateHardLink(link, original, 0))
        {
            return true;
        }

        try
        {
            File.CreateSymbolicLink(link, original);
            return true;
        }
        catch (Exception e)
        {
            log.Debug($"[shadow] no symlink for {Path.GetFileName(original)} either: {e.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Writes one Standard file entry at <paramref name="end" /> and moves it past what was
    ///     written.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The caller carries the offset, and this is the whole performance story.</b> It used
    ///         to ask the stream: <c>Align(dat.Length, 128)</c>, then <c>SetLength</c>, then seek.
    ///         Reading <see cref="FileStream.Length" /> flushes whatever is buffered before it can
    ///         answer, so the buffer never accumulated and every page cost a flush, a file-size query
    ///         and an extend — some fifteen thousand system calls across a pack, which is what a
    ///         rebuild measured in tens of seconds is actually made of. Kept as one sequential
    ///         append, the stream buffers as intended.
    ///     </para>
    ///     <para>
    ///         The alignment gap is <b>written</b> rather than seeked over for the same reason: a
    ///         seek past the end asks the filesystem to extend the file, and a few padding bytes into
    ///         a buffer cost nothing.
    ///     </para>
    ///     <para>
    ///         <b>No staging buffer either.</b> The block table has to carry each block's offset, but
    ///         those are arithmetic — every block is <c>BlockData</c> long except the last, and each
    ///         is padded to 128 — so they can be computed before anything is written instead of
    ///         discovered by writing the body into memory first and copying it out again. That copied
    ///         the whole pack through the heap a second time.
    ///     </para>
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
    ///     Replaces Lumina's <c>ffxiv</c> repository with one over the shadow folder and drops the
    ///     sheet cache.
    /// </summary>
    /// <returns>Null when it worked, or what was missing.</returns>
    private static string? Point(GameData gameData, string shadow)
    {
        var repoType = typeof(GameData).Assembly.GetType("Lumina.Data.Repository");
        if (repoType is null)
        {
            return "Lumina has no Lumina.Data.Repository any more";
        }

        var ctor = repoType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 2
                    && p[0].ParameterType == typeof(DirectoryInfo)
                    && p[1].ParameterType == typeof(GameData);
            });

        if (ctor is null)
        {
            return "Lumina has no Repository(DirectoryInfo, GameData) any more";
        }

        var excel = typeof(GameData).GetProperty("Excel", BindingFlags.Instance | BindingFlags.Public);
        var setExcel = excel?.GetSetMethod(nonPublic: true);
        if (setExcel is null)
        {
            return "GameData.Excel can no longer be assigned";
        }

        if (!gameData.Repositories.ContainsKey("ffxiv"))
        {
            return $"no ffxiv repository to replace; there are {string.Join(", ", gameData.Repositories.Keys)}";
        }

        gameData.Repositories["ffxiv"] =
            (Lumina.Data.Repository)ctor.Invoke([new DirectoryInfo(shadow), gameData]);

        // The old module holds every sheet already read, so it has to go. Its constructor reads the
        // sheet list through the repository above, which is also the first proof the archive is sound.
        setExcel.Invoke(gameData, [new Lumina.Excel.ExcelModule(gameData)]);
        return null;
    }

    private static int Align(long value, int to) => (int)((value + to - 1) / to * to);
}
