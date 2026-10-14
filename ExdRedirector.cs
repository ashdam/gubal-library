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
///     Serves the pack's EXD pages, fonts and ULD layouts from disk.
/// </summary>
internal sealed unsafe class ExdRedirector : IDisposable
{
    /// <summary>Where the loose-file branch expects the UTF-16 path inside the scratch buffer.</summary>
    private const int ScratchPathOffset = 0x21;

    /// <summary>
    ///     Where the scratch buffer's address goes in the descriptor: <b>0x30, not 0x08</b>.
    /// </summary>
    private const int ScratchFieldOffset = 0x30;

    /// <summary>The descriptor's own path field is a fixed 260-character array.</summary>
    internal const int MaxLocalPathLength = 259;

    /// <summary>
    ///     The game's own loose-file reader, the function Penumbra calls for every file it serves.
    /// </summary>
    private const string ReadFileSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? "
        + "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42";

    /// <summary>Where <c>SegmentLength</c> sits in the game's resource parameters. From Penumbra.</summary>
    private const int SegmentLengthOffset = 20;

    private static readonly byte[] ExdSuffix = ".exd"u8.ToArray();
    private static readonly byte[] FontPrefix = Encoding.ASCII.GetBytes(PackContents.FontPrefix);

    private readonly IPluginLog log;
    private readonly Dictionary<string, string> pages;
    private readonly Dictionary<string, string> layouts;
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

    private readonly ResourceScope resources;
    private int servedPages;
    private int servedFonts;
    private int reported;

    private ExdRedirector(
        IGameInteropProvider interop,
        IPluginLog log,
        Dictionary<string, string> pages,
        Dictionary<string, string> layouts,
        IReadOnlyList<PackPage> fontFiles,
        IReadOnlyList<PackPage> screenImages,
        nint readFile,
        TextureLoader? textures,
        PackManifest manifest,
        CompatibilitySelection? compatibility)
    {
        this.log = log;
        this.pages = pages;
        this.layouts = layouts;
        this.readFile = (delegate* unmanaged<FileThread*, FileDescriptor*, int, byte, byte>)readFile;
        this.textures = textures;
        this.resources = new ResourceScope(log);
        if (textures is not null) this.resources.Own(textures);
        this.Manifest = manifest;
        this.Compatibility = compatibility;
        this.FontCount = fontFiles.Count;

        this.fonts = new Dictionary<string, FontEntry>(StringComparer.OrdinalIgnoreCase);
        this.fontsByRooted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var font in fontFiles.Concat(screenImages))
            {
                var entry = this.resources.Own(new FontEntry(font.LocalPath));
                this.fonts[font.GamePath] = entry;
                this.fontsByRooted[entry.Rooted] = font.LocalPath;
            }

            this.hook = this.resources.Own(interop.HookFromAddress<FileThread.Delegates.DoFileJob>(
                FileThread.Addresses.DoFileJob.Value,
                this.Detour));

            // The resource hooks only exist when there are fonts: pages do not need them.
            if (this.fonts.Count > 0)
            {
                this.getSync = this.resources.Own(interop.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(
                    ResourceManager.Addresses.GetResourceSync.Value,
                    this.GetResourceSyncDetour));
                this.getAsync = this.resources.Own(interop.HookFromAddress<ResourceManager.Delegates.GetResourceAsync>(
                    ResourceManager.Addresses.GetResourceAsync.Value,
                    this.GetResourceAsyncDetour));
            }

            this.hook.Enable();
            this.getSync?.Enable();
            this.getAsync?.Enable();
        }
        catch
        {
            this.resources.Dispose();
            throw;
        }

        Diagnostics.Log(log,
            "Serving {Count} rebuilt page(s), {Fonts} font file(s) and {Layouts} ULD file(s) of '{Pack}' ({Version}) from disk; hook at 0x{Address:X}.",
            this.PageCount,
            this.FontCount,
            this.layouts.Count,
            manifest.DisplayName,
            manifest.TranslationVersion ?? "no translationVersion, pack predates the stamp",
            FileThread.Addresses.DoFileJob.Value);
    }

    /// <summary>What the loaded pack says about itself.</summary>
    public PackManifest Manifest { get; }

    public CompatibilitySelection? Compatibility { get; }

    /// <summary>How many page redirections are in place.</summary>
    public int PageCount => this.pages.Count;

    internal IReadOnlyDictionary<string, string> ServedPages => this.pages;

    /// <summary>How many font files are registered. See <see cref="PackContents.FontPrefix" />.</summary>
    public int FontCount { get; }

    /// <summary>How many page reads have actually been answered from disk this session.</summary>
    public int ServedCount => this.servedPages;

    /// <summary>Font reads answered from disk. Counted apart from the pages.</summary>
    public int FontsServedCount => this.servedFonts;

    /// <summary>
    ///     Reads the page directory and starts serving it, or explains why it will not.
    /// </summary>
    /// <param name="contents">The pack, already read. See <see cref="PackContents" /> for why once.</param>
    /// <param name="disabledSheets">The parts the user switched off, from the configuration.</param>
    public static (ExdRedirector? Redirector, string? Error) Create(
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        string directory,
        PackContents contents,
        ICollection<string> disabledSheets,
        bool lifestreamCompatibility,
        IDataManager data,
        string compatibilityCache,
        Func<bool> canWait)
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

        var (compatibility, compatibilityError) = PackResourceCompatibility.Check(directory, manifest,
            RunningGameVersion(), data.GameData.DataPath.FullName, compatibilityCache, canWait,
            message => log.Warning(message));
        if (compatibilityError is not null)
        {
            return (null, compatibilityError);
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

        var pages = contents.Servable(disabledSheets, lifestreamCompatibility);
        var layouts = contents.ServableLayouts(disabledSheets, lifestreamCompatibility);
        var fontFiles = contents.Fonts;
        IReadOnlyList<PackPage> screenImages = contents.ServableScreenImages(disabledSheets, lifestreamCompatibility);
        if (compatibility is not null)
        {
            PackResourceCompatibility.Restrict(compatibility, pages.Keys.Concat(layouts.Keys)
                .Concat(fontFiles.Select(file => file.GamePath)).Concat(screenImages.Select(file => file.GamePath)));
            if (compatibility.RequiredFailed)
                return (null, CheapLoc.Loc.Localize("Compatibility.FontsChanged",
                    "The older pack's required fonts could not be verified. Translation is disabled until the pack is updated."));
            pages = pages.Where(file => compatibility.Allows(file.Key))
                .ToDictionary(file => file.Key, file => file.Value, StringComparer.OrdinalIgnoreCase);
            layouts = layouts.Where(file => compatibility.Allows(file.Key))
                .ToDictionary(file => file.Key, file => file.Value, StringComparer.OrdinalIgnoreCase);
            fontFiles = fontFiles.Where(file => compatibility.Allows(file.GamePath)).ToList();
            screenImages = screenImages.Where(file => compatibility.Allows(file.GamePath)).ToArray();
            foreach (var (path, source) in compatibility.Excluded)
                Diagnostics.Log(log, "Using the game original for {Path}: compatibility source {Source} changed or is unavailable.", path, source);
        }

        contents.LogOmissions(log, disabledSheets);
        if (pages.Count == 0)
        {
            if (compatibility is { Excluded.Count: > 0 })
                return (null, CheapLoc.Loc.Localize("Compatibility.NoPages",
                    "No enabled translated pages match this game version. Update the language pack."));
            return (null, "No pages are enabled. Check the translated parts and compatibility settings.");
        }

        // Required fonts must be served with their translated pages.
        var readFile = nint.Zero;
        TextureLoader? textures = null;
        if (fontFiles.Count + screenImages.Count > 0)
        {
            string? missing = null;
            if (!sigScanner.TryScanText(ReadFileSignature, out readFile))
            {
                missing = "ReadFile";
            }
            else
            {
                var rootedTextures = fontFiles.Concat(screenImages)
                    .Where(f => f.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.LocalPath.Replace('\\', '/'));
                (textures, missing) = TextureLoader.Create(interop, sigScanner, log, rootedTextures);
            }

            if (missing is not null)
            {
                if (fontFiles.Count > 0)
                {
                    textures?.Dispose();
                    return (null, CheapLoc.Loc.Localize("Compatibility.FontsUnavailable",
                        "The required pack fonts could not be loaded. Translation is disabled. Check the plugin log."));
                }
                log.Warning(
                    "{Count} font or screen-image file(s) are not being served: the client's {Function} was not found. "
                    + "The pages are served as usual.",
                    fontFiles.Count + screenImages.Count,
                    missing);
                fontFiles = [];
                screenImages = [];
            }
        }

        try
        {
            return (new ExdRedirector(interop, log, pages, layouts, fontFiles, screenImages, readFile, textures, manifest, compatibility), null);
        }
        catch (Exception e)
        {
            return (null, $"The read hook could not be installed: {e.Message}");
        }
    }

    public void Dispose() => this.resources.Dispose();

    private byte Detour(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync)
    {
        string? local = null;
        var font = false;
        var layout = false;

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
                else if (this.layouts.Count > 0 && IsLayoutPath(name))
                {
                    layout = this.layouts.TryGetValue(Encoding.UTF8.GetString(name), out local);
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
        Span<char> originalPath = stackalloc char[MaxLocalPathLength + 1];
        descriptor->FilePath.CopyTo(originalPath);
        Exception? failure = null;
        byte result = 0;
        try
        {
            result = this.Serve(thread, descriptor, priority, isSync, local, font, layout);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            // The descriptor must not retain a pointer to the reader's temporary stack buffer.
            descriptor->FileMode = mode;
            *(byte**)((byte*)descriptor + ScratchFieldOffset) = scratch;
            originalPath.CopyTo(descriptor->FilePath);
        }

        if (failure is not null)
        {
            this.log.Error(failure, "Could not serve '{Path}'; falling back to the game's own copy.", local);
            return this.hook!.Original(thread, descriptor, priority, isSync);
        }

        return result;
    }

    /// <summary>
    ///     Points the descriptor at a file on disk and lets the game read it.
    /// </summary>
    private byte Serve(
        FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync, string local, bool font, bool layout)
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

        // Trace each font and layout read, and the first page reads.
        var trace = font || layout || Interlocked.Increment(ref this.reported) <= 5;
        if (trace)
        {
            Diagnostics.Log(this.log, "Attempting '{Path}' from disk ({Sync}).", local, isSync ? "sync" : "async");
        }

        // Fonts use ReadFile. Pages and ULD files use the original dispatch.
        byte result;
        if (font)
        {
            result = this.readFile(thread, descriptor, priority, isSync ? (byte)1 : (byte)0);
            if (local.Replace('\\', '/').Contains(PackContents.FontPrefix, StringComparison.OrdinalIgnoreCase))
                this.servedFonts++;
        }
        else
        {
            result = this.hook!.Original(thread, descriptor, priority, isSync);
            if (!layout)
            {
                this.servedPages++;
            }
        }

        if (trace)
        {
            Diagnostics.Log(this.log, "Served '{Path}' from disk (result {Result}).", local, result);
        }

        return result;
    }

    /// <summary>True if the requested path starts with <see cref="PackContents.FontPrefix" />, in any case.</summary>
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
    private static bool IsRooted(ReadOnlySpan<byte> name) =>
        (name.Length >= 1 && name[0] is (byte)'/' or (byte)'\\')
        || (name.Length >= 2
            && name[0] is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')
            && name[1] == (byte)':');

    private static bool IsLayoutPath(ReadOnlySpan<byte> name) =>
        name.Length > 4 && name[^4] == (byte)'.'
        && name[^3] is (byte)'u' or (byte)'U'
        && name[^2] is (byte)'l' or (byte)'L'
        && name[^1] is (byte)'d' or (byte)'D';

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
    private FontEntry? FontFor(CStringPointer path, void* parameters)
    {
        try
        {
            if (!path.HasValue)
            {
                return null;
            }

            var name = path.AsSpan();
            if (!(IsFontPath(name) || PackContents.IsScreenImagePath(name)) || !this.fonts.TryGetValue(Encoding.UTF8.GetString(name), out var entry))
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
