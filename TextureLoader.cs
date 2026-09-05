using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Lumina.Misc;

namespace GubalLibrary;

/// <summary>
///     Makes the client accept a texture that comes from disk instead of from its archives.
/// </summary>
/// <remarks>
///     <para>
///         A texture resource is not loaded like a page. On load, the client checks the file's
///         CRC-64 with its RSF service, and a file it does not know fails the load. The client
///         waits on a failed font texture and does not enter the world.
///     </para>
///     <para>
///         This is Penumbra's answer, for our files only. The CRC check is hooked to say "unknown"
///         for our textures and to flag the thread. The load function is hooked to see the flag
///         after the failed load, and to call the client's own local-file texture loader instead.
///         All five patterns are Penumbra's. If one of them is not found, fonts are refused at
///         load and the pages are served as usual.
///     </para>
/// </remarks>
internal sealed unsafe class TextureLoader : IDisposable
{
    private const string CheckFileStateSignature = "E8 ?? ?? ?? ?? 48 85 C0 74 ?? 4C 8B C8 44 0F B6 C5";

    private const string TexHandleOnLoadSignature =
        "40 53 55 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B D9";

    private const string LoadTexFileLocalSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 49 8B E8 44 88 4C 24";

    private const string TexHandleUpdateCategorySignature = "0F B7 41 ?? FF C8";

    private const string LodConfigSignature = "48 8B 05 ?? ?? ?? ?? B3";

    private readonly IPluginLog log;
    private readonly HashSet<ulong> ours;
    private readonly Hook<CheckFileStateDelegate> checkFileState;
    private readonly Hook<TexHandleOnLoadDelegate> onLoad;
    private readonly delegate* unmanaged<TextureResourceHandle*, int, FileDescriptor*, byte, byte> loadTexFileLocal;
    private readonly delegate* unmanaged<TextureResourceHandle*, void> updateCategory;
    private readonly nint lodConfig;

    /// <summary>Set by the CRC check and read by the load hook, on the same thread.</summary>
    private readonly ThreadLocal<bool> failedOnPurpose = new(() => false);

    private TextureLoader(
        IGameInteropProvider interop,
        IPluginLog log,
        HashSet<ulong> ours,
        nint checkFileState,
        nint onLoad,
        nint loadTexFileLocal,
        nint updateCategory,
        nint lodConfig)
    {
        this.log = log;
        this.ours = ours;
        this.loadTexFileLocal =
            (delegate* unmanaged<TextureResourceHandle*, int, FileDescriptor*, byte, byte>)loadTexFileLocal;
        this.updateCategory = (delegate* unmanaged<TextureResourceHandle*, void>)updateCategory;
        this.lodConfig = lodConfig;

        this.checkFileState = interop.HookFromAddress<CheckFileStateDelegate>(checkFileState, this.CheckFileStateDetour);
        this.onLoad = interop.HookFromAddress<TexHandleOnLoadDelegate>(onLoad, this.OnLoadDetour);
        this.checkFileState.Enable();
        this.onLoad.Enable();
    }

    private delegate nint CheckFileStateDelegate(nint service, ulong crc64);

    private delegate byte TexHandleOnLoadDelegate(TextureResourceHandle* handle, FileDescriptor* descriptor, byte unk);

    /// <summary>
    ///     Finds the five functions and installs the two hooks, or says which pattern failed.
    /// </summary>
    /// <param name="rootedTexturePaths">The textures to accept, as the rooted paths their resources carry.</param>
    public static (TextureLoader? Loader, string? Error) Create(
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        IEnumerable<string> rootedTexturePaths)
    {
        if (!sigScanner.TryScanText(CheckFileStateSignature, out var checkFileState))
        {
            return (null, "CheckFileState");
        }

        if (!sigScanner.TryScanText(TexHandleOnLoadSignature, out var onLoad))
        {
            return (null, "TexHandleOnLoad");
        }

        if (!sigScanner.TryScanText(LoadTexFileLocalSignature, out var loadTexFileLocal))
        {
            return (null, "LoadTexFileLocal");
        }

        if (!sigScanner.TryScanText(TexHandleUpdateCategorySignature, out var updateCategory))
        {
            return (null, "TexHandleUpdateCategory");
        }

        if (!sigScanner.TryGetStaticAddressFromSig(LodConfigSignature, out var lodConfig))
        {
            return (null, "LodConfig");
        }

        var ours = new HashSet<ulong>();
        foreach (var path in rootedTexturePaths)
        {
            ours.Add(Crc64(path));
        }

        try
        {
            return (new TextureLoader(
                interop, log, ours, checkFileState, onLoad, loadTexFileLocal, updateCategory, lodConfig), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    public void Dispose()
    {
        this.onLoad.Disable();
        this.onLoad.Dispose();
        this.checkFileState.Disable();
        this.checkFileState.Dispose();
        this.failedOnPurpose.Dispose();
    }

    /// <summary>The client's file key: CRC-32 of the folder in the high word, CRC-32 of the name in the low word, both lower case.</summary>
    /// <remarks>The same key the archive index uses. See <see cref="GubalLumina" />.</remarks>
    private static ulong Crc64(string rootedPath)
    {
        var lower = rootedPath.ToLowerInvariant();
        var slash = lower.LastIndexOf('/');
        return ((ulong)Crc32.Get(lower[..slash]) << 32) | Crc32.Get(lower[(slash + 1)..]);
    }

    /// <summary>Says "unknown file" for our textures and flags the thread. Everything else goes to the client.</summary>
    private nint CheckFileStateDetour(nint service, ulong crc64)
    {
        if (this.ours.Contains(crc64))
        {
            this.failedOnPurpose.Value = true;
            return nint.Zero;
        }

        return this.checkFileState.Original(service, crc64);
    }

    /// <summary>After a load that failed on our flag, loads the texture with the client's local-file loader.</summary>
    private byte OnLoadDetour(TextureResourceHandle* handle, FileDescriptor* descriptor, byte unk)
    {
        var result = this.onLoad.Original(handle, descriptor, unk);
        if (!this.failedOnPurpose.Value)
        {
            return result;
        }

        this.failedOnPurpose.Value = false;

        try
        {
            result = this.loadTexFileLocal(handle, this.Lod(handle), descriptor, unk);
            this.updateCategory(handle);
            Diagnostics.Log(this.log, "Texture '{Path}' loaded as a local file (result {Result}).", handle->FileName.ToString(), result);
        }
        catch (Exception e)
        {
            this.log.Error(e, "The local texture load failed for '{Path}'.", handle->FileName.ToString());
        }

        return result;
    }

    /// <summary>The level of detail the client's own texture load would pick. Copied from Penumbra as is.</summary>
    private int Lod(TextureResourceHandle* handle)
    {
        if (handle->ChangeLod)
        {
            var config = *(byte*)this.lodConfig + 0xE;
            if (config == byte.MaxValue)
            {
                return 2;
            }
        }

        return 0;
    }
}
