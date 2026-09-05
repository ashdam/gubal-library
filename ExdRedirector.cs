using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using InteropGenerator.Runtime;
using Lumina.Misc;

// The game's read modes, not System.IO's. Both are called FileMode and this file names one of them
// in a line where getting it wrong is a wrong constant rather than a compiler error.
using FileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace GubalLibrary;

/// <summary>
///     Gives the game the rebuilt <c>.exd</c> pages of a pack, and its fonts if it has some, in
///     place of the files in its archives.
/// </summary>
/// <remarks>
///     <para>
///         The file route, owned rather than borrowed. With <c>LoadRequiredState: 2</c> this plugin
///         attaches 2.1 seconds before the client's first Excel read, so a redirection installed in
///         the constructor covers every sheet loaded at boot — including the ones a mid-session
///         Penumbra mod could never reach.
///     </para>
///     <para>
///         <b>The addresses for pages are not Penumbra's.</b> They come from FFXIVClientStructs,
///         which is MIT, ships with Dalamud and is repaired by the ecosystem within hours of a
///         patch — so the fragile part, finding a function in a recompiled client, is not this
///         project's problem. Fonts need six patterns that are Penumbra's:
///         <see cref="ReadFileSignature" /> here and five in <see cref="TextureLoader" />. When one
///         of them is not found, the fonts are refused and the pages are served as usual.
///     </para>
///     <para>
///         <b>The naming is crossed between the two projects.</b> Penumbra's <c>ReadSqPack</c> —
///         <c>40 56 41 56 48 83 EC ?? 0F BE 02</c> — is FFXIVClientStructs'
///         <see cref="FileThread.DoFileJob" />, whose fourth instruction reads
///         <see cref="FileDescriptor.FileMode" />: the function that dispatches on HOW to read, which
///         is what must be intercepted. FFXIVClientStructs also has a <c>FileThread.ReadSqPack</c>,
///         which sees every read and can redirect none of them.
///     </para>
///     <para>
///         The redirection is three fields: mode to <see cref="FileMode.LoadUnpackedResource" />,
///         descriptor pointed at a scratch buffer holding the local path in UTF-16, then let the
///         dispatcher take its loose-file branch. The page is read, parsed and drawn by the game's own
///         code, which is why italics and inverted punctuation survive it.
///     </para>
/// </remarks>
internal sealed unsafe class ExdRedirector : IDisposable
{
    /// <summary>Where the loose-file branch expects the UTF-16 path inside the scratch buffer.</summary>
    /// <remarks>The odd offset is deliberate: the path is not two-byte aligned and must not be made to be.</remarks>
    private const int ScratchPathOffset = 0x21;

    /// <summary>
    ///     Where the scratch buffer's address goes in the descriptor: <b>0x30, not 0x08</b>.
    /// </summary>
    /// <remarks>
    ///     This field crashed the client, so it is a named constant. FFXIVClientStructs calls 0x30
    ///     <c>FileInterface</c> and 0x08 <c>FileBuffer</c>; the loose-file branch reads its path out
    ///     of 0x30, and 0x08 holds the buffer the game means to read INTO — so writing there both
    ///     leaves 0x30 stale and aims the game's own read at a dead stack address. Verified against
    ///     Penumbra's <c>SeFileDescriptor</c>, which agrees on all four offsets and differs only in
    ///     the names.
    /// </remarks>
    private const int ScratchFieldOffset = 0x30;

    /// <summary>The descriptor's own path field is a fixed 260-character array.</summary>
    /// <remarks>
    ///     A longer local path is refused at registration with a line in the log rather than
    ///     truncated into a file that does not exist. Penumbra works around it with a second hook on
    ///     <c>CreateFileW</c>, which is worth it for arbitrary mod folders and not for one directory
    ///     the user chose.
    /// </remarks>
    internal const int MaxLocalPathLength = 259;

    /// <summary>
    ///     The game's own loose-file reader, the function Penumbra calls for every file it serves.
    /// </summary>
    /// <remarks>
    ///     Fonts go through it, and pages do not. A font must not go back to the original
    ///     <see cref="FileThread.DoFileJob" /> with the mode switched: that route does not complete
    ///     a texture. The pattern is Penumbra's, and the scan is unique in the current client.
    /// </remarks>
    private const string ReadFileSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? "
        + "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42";

    /// <summary>Where <c>SegmentLength</c> sits in the game's resource parameters. From Penumbra.</summary>
    /// <remarks>A non-zero length is a partial read, and the hash then covers the segment too. Fonts are never read that way.</remarks>
    private const int SegmentLengthOffset = 20;

    private static readonly byte[] ExdSuffix = ".exd"u8.ToArray();
    private static readonly byte[] FontPrefix = Encoding.ASCII.GetBytes(PackContents.FontPrefix);

    private readonly IPluginLog log;
    private readonly Dictionary<string, string> pages;
    private readonly Hook<FileThread.Delegates.DoFileJob>? hook;

    /// <summary>The fonts by game path, for the resource hooks.</summary>
    private readonly Dictionary<string, FontEntry> fonts;

    /// <summary>The fonts by the rooted path the resource now carries, for the read hook.</summary>
    private readonly Dictionary<string, string> fontsByRooted;

    private readonly Hook<ResourceManager.Delegates.GetResourceSync>? getSync;
    private readonly Hook<ResourceManager.Delegates.GetResourceAsync>? getAsync;
    private readonly delegate* unmanaged<FileThread*, FileDescriptor*, int, byte, byte> readFile;

    /// <summary>Makes the client accept the pack's textures. Null when the pack has no fonts.</summary>
    private readonly TextureLoader? textures;

    private int servedPages;
    private int servedFonts;
    private int reported;

    private ExdRedirector(
        IGameInteropProvider interop,
        IPluginLog log,
        Dictionary<string, string> pages,
        IReadOnlyList<PackPage> fontFiles,
        nint readFile,
        TextureLoader? textures,
        PackManifest manifest)
    {
        this.log = log;
        this.pages = pages;
        this.readFile = (delegate* unmanaged<FileThread*, FileDescriptor*, int, byte, byte>)readFile;
        this.textures = textures;
        this.Manifest = manifest;

        this.fonts = new Dictionary<string, FontEntry>(StringComparer.OrdinalIgnoreCase);
        this.fontsByRooted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var font in fontFiles)
        {
            var entry = new FontEntry(font.LocalPath);
            this.fonts[font.GamePath] = entry;
            this.fontsByRooted[entry.Rooted] = font.LocalPath;
        }

        this.hook = interop.HookFromAddress<FileThread.Delegates.DoFileJob>(
            FileThread.Addresses.DoFileJob.Value,
            this.Detour);

        this.hook.Enable();

        // The resource hooks only exist when there are fonts: pages do not need them.
        if (this.fonts.Count > 0)
        {
            this.getSync = interop.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(
                ResourceManager.Addresses.GetResourceSync.Value,
                this.GetResourceSyncDetour);
            this.getAsync = interop.HookFromAddress<ResourceManager.Delegates.GetResourceAsync>(
                ResourceManager.Addresses.GetResourceAsync.Value,
                this.GetResourceAsyncDetour);
            this.getSync.Enable();
            this.getAsync.Enable();
        }

        Diagnostics.Log(log,
            "Serving {Count} rebuilt page(s) and {Fonts} font file(s) of '{Pack}' ({Version}) from disk; hook at 0x{Address:X}.",
            this.PageCount,
            this.FontCount,
            manifest.DisplayName,
            manifest.TranslationVersion ?? "no translationVersion, pack predates the stamp",
            FileThread.Addresses.DoFileJob.Value);
    }

    /// <summary>What the loaded pack says about itself.</summary>
    public PackManifest Manifest { get; }

    /// <summary>How many page redirections are in place.</summary>
    public int PageCount => this.pages.Count;

    /// <summary>How many font files are registered. See <see cref="PackContents.FontPrefix" />.</summary>
    public int FontCount => this.fonts.Count;

    /// <summary>How many page reads have actually been answered from disk this session.</summary>
    /// <remarks>
    ///     The number that separates "registered" from "working". A redirection installed but never
    ///     hit looks identical to one doing its job, which is the state the Penumbra route sat in for
    ///     a whole session.
    /// </remarks>
    public int ServedCount => this.servedPages;

    /// <summary>Font reads answered from disk. Counted apart from the pages.</summary>
    /// <remarks>
    ///     The client loads its fonts once, at boot. Fonts registered and never served means the
    ///     client read them before this hook. The page count cannot show that.
    /// </remarks>
    public int FontsServedCount => this.servedFonts;

    /// <summary>
    ///     Reads the page directory and starts serving it, or explains why it will not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The manifest check is a refusal, not a warning. The build-time identity gate proves the
    ///         pages reproduce the game's bytes <em>for the patch they were built against</em>, and
    ///         nothing checks that at run time: pages from one patch served to the next shift rows and
    ///         put Spanish on the wrong ones, silently. Losing the Spanish is the better failure.
    ///     </para>
    ///     <para>
    ///         Nothing is hooked until there is something to serve. A detour on a core read path that
    ///         redirects nothing is pure risk, and switching every part off reaches that state by a
    ///         different road, so it is refused in its own words rather than as an empty folder.
    ///     </para>
    /// </remarks>
    /// <param name="contents">The pack, already read. See <see cref="PackContents" /> for why once.</param>
    /// <param name="disabledSheets">The parts the user switched off, from the configuration.</param>
    public static (ExdRedirector? Redirector, string? Error) Create(
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        string directory,
        PackContents contents,
        ICollection<string> disabledSheets)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (null, $"No language pack at '{directory}'.");
        }

        var (manifest, manifestError) = PackManifest.Read(directory);
        if (manifest is null)
        {
            return (null, manifestError);
        }

        var builtFor = manifest.GameVersion;
        var running = RunningGameVersion();
        if (builtFor is null || running is null)
        {
            return (null, $"Cannot compare versions (manifest: {builtFor ?? "none"}, game: {running ?? "unknown"}).");
        }

        if (!string.Equals(builtFor, running, StringComparison.Ordinal))
        {
            return (null,
                $"These pages were built for game {builtFor} but the game is running {running}. "
                + "Regenerate them; serving them now would put translated text on the wrong rows.");
        }

        if (contents.TooLong > 0)
        {
            log.Warning(
                "{Count} page(s) sit at a path longer than {Max} characters and will not be served. "
                + "Install the language pack somewhere with a shorter path.",
                contents.TooLong,
                MaxLocalPathLength);
        }

        // Fonts alone do not make a pack. They only draw the pages.
        if (contents.PageCount == 0)
        {
            return (null, "That folder holds no .exd files, so it is not a language pack.");
        }

        // Fonts need ReadFile and the texture loader. Without them the pages are still served, and
        // the fonts are refused with a line in the log rather than a client that never loads.
        var readFile = nint.Zero;
        TextureLoader? textures = null;
        var fontFiles = contents.Fonts;
        if (fontFiles.Count > 0)
        {
            string? missing = null;
            if (!sigScanner.TryScanText(ReadFileSignature, out readFile))
            {
                missing = "ReadFile";
            }
            else
            {
                var rootedTextures = fontFiles
                    .Where(f => f.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.LocalPath.Replace('\\', '/'));
                (textures, missing) = TextureLoader.Create(interop, sigScanner, log, rootedTextures);
            }

            if (missing is not null)
            {
                log.Warning(
                    "{Count} font file(s) are not being served: the client's {Function} was not found. "
                    + "The pages are served as usual.",
                    fontFiles.Count,
                    missing);
                fontFiles = [];
            }
        }

        var pages = contents.Servable(disabledSheets);
        contents.LogOmissions(log, disabledSheets);

        // Told apart from the empty folder above, because the two have opposite answers: one is a
        // pack that is not there, the other a pack that is there and was asked to stay quiet.
        if (pages.Count == 0)
        {
            return (null,
                "Every part of this language pack is switched off, so there is nothing to serve. "
                + "Turn something back on under Translated parts.");
        }

        try
        {
            return (new ExdRedirector(interop, log, pages, fontFiles, readFile, textures, manifest), null);
        }
        catch (Exception e)
        {
            return (null, $"The read hook could not be installed: {e.Message}");
        }
    }

    public void Dispose()
    {
        this.getSync?.Disable();
        this.getSync?.Dispose();
        this.getAsync?.Disable();
        this.getAsync?.Dispose();
        this.hook?.Disable();
        this.hook?.Dispose();
        this.textures?.Dispose();

        // The game copies the path into the handle, so the buffers are ours to free.
        foreach (var entry in this.fonts.Values)
        {
            entry.Dispose();
        }
    }

    /// <summary>
    ///     Answers a read from our folder when the path is one of ours, and stands aside otherwise.
    /// </summary>
    /// <remarks>
    ///     Every path out calls the original, and the whole body is guarded. This sits on the one
    ///     function every file in the game goes through — models, textures, sound — so an escaping
    ///     exception would not lose a line of Spanish, it would take the client down.
    /// </remarks>
    private byte Detour(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync)
    {
        string? local = null;
        var font = false;

        try
        {
            if (descriptor is not null && descriptor->ResourceHandle is not null)
            {
                var name = descriptor->ResourceHandle->FileName.AsSpan();

                // Check the suffix and the first bytes on the raw path, before any allocation. This
                // runs for every file the client reads, and almost none of them are pages or fonts.
                // A font arrives here under its rooted path: see GetResource.
                if (name.Length > ExdSuffix.Length && name[^ExdSuffix.Length..].SequenceEqual(ExdSuffix))
                {
                    this.pages.TryGetValue(Encoding.UTF8.GetString(name), out local);
                }
                else if (this.fonts.Count > 0 && IsRooted(name))
                {
                    font = this.fontsByRooted.TryGetValue(Encoding.UTF8.GetString(name), out local);
                }
            }
        }
        catch (Exception e)
        {
            this.log.Error(e, "Could not read the requested path; the read is being passed through.");
            local = null;
        }

        if (local is null)
        {
            return this.hook!.Original(thread, descriptor, priority, isSync);
        }

        var mode = descriptor->FileMode;
        var scratch = *(byte**)((byte*)descriptor + ScratchFieldOffset);
        try
        {
            return this.Serve(thread, descriptor, priority, isSync, local, font);
        }
        catch (Exception e)
        {
            // Both fields go back, not just the mode: a passthrough with our scratch pointer still in
            // place aims the game's own read at a stack frame that is about to go away.
            descriptor->FileMode = mode;
            *(byte**)((byte*)descriptor + ScratchFieldOffset) = scratch;
            this.log.Error(e, "Could not serve '{Path}'; falling back to the game's own copy.", local);
            return this.hook!.Original(thread, descriptor, priority, isSync);
        }
    }

    /// <summary>
    ///     Points the descriptor at a file on disk and lets the game read it.
    /// </summary>
    /// <remarks>
    ///     The path is written twice on purpose: the loose-file branch reads it out of the scratch
    ///     buffer, and the descriptor's own <c>FilePath</c> is what the rest of the client reports the
    ///     file as. The scratch buffer is on the stack, which is sound because the call below
    ///     completes before this frame goes away — asynchronous reads copy what they need out first.
    /// </remarks>
    private byte Serve(
        FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync, string local, bool font)
    {
        var size = ScratchPathOffset + ((local.Length + 1) * sizeof(char));
        var scratch = stackalloc byte[size];
        new Span<byte>(scratch, size).Clear();

        var target = new Span<char>(scratch + ScratchPathOffset, local.Length + 1);
        local.CopyTo(target);
        target[local.Length] = '\0';

        var filePath = descriptor->FilePath;
        local.CopyTo(filePath);
        filePath[local.Length] = '\0';

        // Written through a raw offset rather than the generated field, which is typed FileInterface*
        // and is not one. See ScratchFieldOffset.
        *(byte**)((byte*)descriptor + ScratchFieldOffset) = scratch;
        descriptor->FileMode = FileMode.LoadUnpackedResource;

        // Log before and after, for the first pages and for every font. The pair is the point: the
        // first attempt at this crashed the client inside the game's read, and nothing said which
        // page it died on. Fonts are few and read once, so each one gets a line.
        var trace = font || this.reported < 5;
        if (trace)
        {
            if (!font)
            {
                this.reported++;
            }

            Diagnostics.Log(this.log, "Attempting '{Path}' from disk ({Sync}).", local, isSync ? "sync" : "async");
        }

        // Fonts through ReadFile, pages through the original dispatch. See ReadFileSignature.
        byte result;
        if (font)
        {
            result = this.readFile(thread, descriptor, priority, isSync ? (byte)1 : (byte)0);
            this.servedFonts++;
        }
        else
        {
            result = this.hook!.Original(thread, descriptor, priority, isSync);
            this.servedPages++;
        }

        if (trace)
        {
            Diagnostics.Log(this.log, "Served '{Path}' from disk (result {Result}).", local, result);
        }

        return result;
    }

    /// <summary>True if the requested path starts with <see cref="PackContents.FontPrefix" />, in any case.</summary>
    /// <remarks>ASCII case folding by hand, on the raw bytes. This runs for every file the client reads.</remarks>
    private static bool IsFontPath(ReadOnlySpan<byte> name)
    {
        if (name.Length <= FontPrefix.Length)
        {
            return false;
        }

        for (var i = 0; i < FontPrefix.Length; i++)
        {
            var c = name[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c += 32;
            }

            if (c != FontPrefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True if the path starts with a drive letter and a colon, or with a separator.</summary>
    /// <remarks>Only our own redirections, and Penumbra's, put such a path in a resource handle.</remarks>
    private static bool IsRooted(ReadOnlySpan<byte> name) =>
        (name.Length >= 1 && name[0] is (byte)'/' or (byte)'\\')
        || (name.Length >= 2
            && name[0] is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')
            && name[1] == (byte)':');

    private ResourceHandle* GetResourceSyncDetour(
        ResourceManager* manager,
        ResourceCategory* category,
        uint* type,
        uint* hash,
        CStringPointer path,
        void* parameters,
        void* debugPtr,
        uint debugInt)
    {
        var entry = this.FontFor(path, parameters);
        if (entry is null)
        {
            return this.getSync!.Original(manager, category, type, hash, path, parameters, debugPtr, debugInt);
        }

        var ours = entry.Hash;
        return this.getSync!.Original(manager, category, type, &ours, entry.Utf8, parameters, debugPtr, debugInt);
    }

    private ResourceHandle* GetResourceAsyncDetour(
        ResourceManager* manager,
        ResourceCategory* category,
        uint* type,
        uint* hash,
        CStringPointer path,
        void* parameters,
        bool isUnknown,
        void* debugPtr,
        uint debugInt)
    {
        var entry = this.FontFor(path, parameters);
        if (entry is null)
        {
            return this.getAsync!.Original(manager, category, type, hash, path, parameters, isUnknown, debugPtr, debugInt);
        }

        var ours = entry.Hash;
        return this.getAsync!.Original(manager, category, type, &ours, entry.Utf8, parameters, isUnknown, debugPtr, debugInt);
    }

    /// <summary>
    ///     The font to swap in for a requested resource, or null to leave the request alone.
    /// </summary>
    /// <remarks>
    ///     This is Penumbra's route. The resource must be created under the rooted path and its
    ///     hash, so the client treats it as a loose file from the start. A redirection at read time
    ///     only is not enough for a texture: see <see cref="TextureLoader" />.
    /// </remarks>
    private FontEntry? FontFor(CStringPointer path, void* parameters)
    {
        try
        {
            if (!path.HasValue)
            {
                return null;
            }

            var name = path.AsSpan();
            if (!IsFontPath(name) || !this.fonts.TryGetValue(Encoding.UTF8.GetString(name), out var entry))
            {
                return null;
            }

            // A partial read hashes the segment too, and a font is never read in parts.
            if (parameters is not null && *(uint*)((byte*)parameters + SegmentLengthOffset) != 0)
            {
                return null;
            }

            if (!entry.Reported)
            {
                entry.Reported = true;
                Diagnostics.Log(this.log, "Resource '{Game}' redirected to '{Local}'.", Encoding.UTF8.GetString(name), entry.Rooted);
            }

            return entry;
        }
        catch (Exception e)
        {
            this.log.Error(e, "Could not inspect a resource request; it is being passed through.");
            return null;
        }
    }

    /// <summary>One font file of the pack, in the forms the two hooks need.</summary>
    private sealed unsafe class FontEntry : IDisposable
    {
        /// <summary>The path hash the client keys the resource by: CRC-32 of the lower-case rooted path.</summary>
        public uint Hash { get; }

        public FontEntry(string localPath)
        {
            // Forward slashes, as Penumbra passes them. Windows accepts both.
            this.Rooted = localPath.Replace('\\', '/');
            this.Hash = Crc32.Get(this.Rooted.ToLowerInvariant());

            var bytes = Encoding.UTF8.GetBytes(this.Rooted);
            this.Utf8 = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(this.Utf8, bytes.Length));
            this.Utf8[bytes.Length] = 0;
        }

        /// <summary>The path with forward slashes, as the resource handle will carry it.</summary>
        public string Rooted { get; }

        /// <summary>The same path as a null-terminated UTF-8 buffer, alive as long as the redirector.</summary>
        public byte* Utf8 { get; }

        /// <summary>Set after the first line in the log about this font.</summary>
        public bool Reported { get; set; }

        public void Dispose() => NativeMemory.Free(this.Utf8);
    }

    /// <summary>The patch the client is running, from <c>ffxivgame.ver</c> beside the executable.</summary>
    /// <remarks>The same file the pack builder stamps from, so both sides of the comparison share a source.</remarks>
    internal static string? RunningGameVersion()
    {
        try
        {
            var gameDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (gameDir is null)
            {
                return null;
            }

            var file = Path.Combine(gameDir, "ffxivgame.ver");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
