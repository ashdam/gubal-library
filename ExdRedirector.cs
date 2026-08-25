using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.File;

// The game's read modes, not System.IO's. Both are called FileMode and this file names one of them
// in a line where getting it wrong is a wrong constant rather than a compiler error.
using FileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace GubalLibrary;

/// <summary>
///     Hands the game rebuilt Spanish <c>.exd</c> pages in place of the ones inside its archives.
/// </summary>
/// <remarks>
///     <para>
///         The file route, owned rather than borrowed. With <c>LoadRequiredState: 2</c> this plugin
///         attaches 2.1 seconds before the client's first Excel read, so a redirection installed in
///         the constructor covers every sheet loaded at boot — including the ones a mid-session
///         Penumbra mod could never reach.
///     </para>
///     <para>
///         <b>Nothing here is Penumbra's.</b> Both addresses come from FFXIVClientStructs, which is
///         MIT, ships with Dalamud and is repaired by the ecosystem within hours of a patch — so the
///         fragile part, finding a function in a recompiled client, is not this project's problem.
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

    private static readonly byte[] ExdSuffix = ".exd"u8.ToArray();

    private readonly IPluginLog log;
    private readonly Dictionary<string, string> pages;
    private readonly Hook<FileThread.Delegates.DoFileJob>? hook;

    private int served;
    private int reported;

    private ExdRedirector(
        IGameInteropProvider interop, IPluginLog log, Dictionary<string, string> pages, PackManifest manifest)
    {
        this.log = log;
        this.pages = pages;
        this.Manifest = manifest;

        this.hook = interop.HookFromAddress<FileThread.Delegates.DoFileJob>(
            FileThread.Addresses.DoFileJob.Value,
            this.Detour);

        this.hook.Enable();

        log.Information(
            "Serving {Count} rebuilt page(s) of '{Pack}' ({Version}) from disk; hook at 0x{Address:X}.",
            pages.Count,
            manifest.DisplayName,
            manifest.TranslationVersion ?? "no translationVersion — pack predates the stamp",
            FileThread.Addresses.DoFileJob.Value);
    }

    /// <summary>What the loaded pack says about itself.</summary>
    public PackManifest Manifest { get; }

    /// <summary>How many redirections are in place.</summary>
    public int PageCount => this.pages.Count;

    /// <summary>How many reads have actually been answered from disk this session.</summary>
    /// <remarks>
    ///     The number that separates "registered" from "working". A redirection installed but never
    ///     hit looks identical to one doing its job, which is the state the Penumbra route sat in for
    ///     a whole session.
    /// </remarks>
    public int ServedCount => this.served;

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
                $"These pages were built for game {builtFor} but the client is running {running}. "
                + "Regenerate them; serving them now would put Spanish on the wrong rows.");
        }

        if (contents.TooLong > 0)
        {
            log.Warning(
                "{Count} page(s) sit at a path longer than {Max} characters and will not be served. "
                + "Install the language pack somewhere with a shorter path.",
                contents.TooLong,
                MaxLocalPathLength);
        }

        if (contents.PageCount == 0)
        {
            return (null, "That folder holds no .exd files, so it is not a language pack.");
        }

        var pages = contents.Servable(disabledSheets);
        contents.LogOmissions(log, disabledSheets, pages.Count);

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
            return (new ExdRedirector(interop, log, pages, manifest), null);
        }
        catch (Exception e)
        {
            return (null, $"The read hook could not be installed: {e.Message}");
        }
    }

    public void Dispose()
    {
        this.hook?.Disable();
        this.hook?.Dispose();
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

        try
        {
            if (descriptor is not null && descriptor->ResourceHandle is not null)
            {
                var name = descriptor->ResourceHandle->FileName.AsSpan();

                // Suffix checked on the raw bytes before anything is allocated: this runs for every
                // file the client reads and almost none of them are Excel pages.
                if (name.Length > ExdSuffix.Length && name[^ExdSuffix.Length..].SequenceEqual(ExdSuffix))
                {
                    this.pages.TryGetValue(Encoding.UTF8.GetString(name), out local);
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
            return this.Serve(thread, descriptor, priority, isSync, local);
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
        FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync, string local)
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

        // Logged before and after, for the first few, and the pair is the point: the first attempt at
        // this took the client down inside the game's read, so nothing said which page it died on.
        var trace = this.reported < 5;
        if (trace)
        {
            this.reported++;
            this.log.Information("Attempting '{Path}' from disk ({Sync}).", local, isSync ? "sync" : "async");
        }

        var result = this.hook!.Original(thread, descriptor, priority, isSync);
        this.served++;

        if (trace)
        {
            this.log.Information("Served '{Path}' from disk (result {Result}).", local, result);
        }

        return result;
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
