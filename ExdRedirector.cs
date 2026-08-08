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
///         The file route, owned rather than borrowed. It replaces asking Penumbra to do this over
///         IPC, and the reason it can is a measurement rather than a preference: with
///         <c>LoadRequiredState: 2</c> in the manifest this plugin attaches 2.1 seconds before the
///         client's first Excel read, so a redirection installed in the constructor is in place for
///         every sheet the game loads at boot — including the ones a mid-session Penumbra mod could
///         never reach, which is what sent the guildhest descriptions back to English.
///     </para>
///     <para>
///         <b>Nothing here is Penumbra's.</b> Both addresses come from FFXIVClientStructs, which is
///         MIT, ships with Dalamud, and is repaired by the whole ecosystem within hours of a patch —
///         so the fragile part, finding a function inside a recompiled client, is not this project's
///         problem. That was the objection to doing this ourselves and it turned out to be wrong: the
///         patterns are maintained, just under different names on each side.
///     </para>
///     <para>
///         <b>The naming is crossed between the two projects and it matters.</b> What Penumbra calls
///         <c>ReadSqPack</c> — <c>40 56 41 56 48 83 EC ?? 0F BE 02</c> — is what FFXIVClientStructs
///         calls <see cref="FileThread.DoFileJob" />. Its fourth instruction, <c>movsx eax, byte
///         ptr [rdx]</c>, reads <see cref="FileDescriptor.FileMode" /> at offset zero: this is the
///         function that dispatches on how a file is to be read, which is exactly what has to be
///         intercepted. FFXIVClientStructs also has a <c>FileThread.ReadSqPack</c>, on a different
///         pattern, further down. Hooking that one sees every read and can redirect none of them.
///     </para>
///     <para>
///         The redirection itself is three fields. Set the mode to
///         <see cref="FileMode.LoadUnpackedResource" />, point the descriptor at a scratch buffer
///         holding the local path in UTF-16, and let the dispatcher take its loose-file branch. The
///         page is then read from disk by the game's own code, parsed by the game's own Excel reader
///         and drawn by the game's own text pipeline — which is the whole point of this route, and
///         why italics and inverted punctuation survive it when injection had to be taught each one.
///     </para>
/// </remarks>
internal sealed unsafe class ExdRedirector : IDisposable
{
    /// <summary>Where the loose-file branch expects the UTF-16 path inside the scratch buffer.</summary>
    /// <remarks>
    ///     Not a guess and not adjustable: it is where the game reads from. The odd byte offset is
    ///     deliberate — the path is not aligned to a two-byte boundary and must not be made to be.
    /// </remarks>
    private const int ScratchPathOffset = 0x21;

    /// <summary>
    ///     Where the scratch buffer's address goes in the descriptor: <b>0x30, not 0x08</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This one field crashed the client, so it is a named constant with the reasoning
    ///         attached rather than a member access that reads plausibly either way. FFXIVClientStructs
    ///         calls 0x30 <c>FileInterface</c> and 0x08 <c>FileBuffer</c>; the loose-file branch reads
    ///         its path out of 0x30, and 0x08 is where the game has put the buffer it intends to read
    ///         the bytes <em>into</em>. Writing to 0x08 therefore does two wrong things at once —
    ///         leaves 0x30 holding whatever it held, and points the game's own read at a dead stack
    ///         address — and the second one is what took the client down.
    ///     </para>
    ///     <para>
    ///         Verified against Penumbra's <c>SeFileDescriptor</c>, whose field at 0x30 is the one it
    ///         writes and whose other three offsets — mode at 0x00, handle at 0x50, path at 0x70 —
    ///         agree with FFXIVClientStructs exactly. Only the names differ, which is what made the
    ///         wrong field read like the right one.
    ///     </para>
    /// </remarks>
    private const int ScratchFieldOffset = 0x30;

    /// <summary>The descriptor's own path field is a fixed 260-character array.</summary>
    /// <remarks>
    ///     A local path longer than this cannot be written into it, so such a page is refused at
    ///     registration with a line in the log rather than truncated into a file that does not exist.
    ///     Penumbra works around the limit with a second hook on <c>CreateFileW</c>; that is worth it
    ///     for arbitrary user mod folders and not for ours, which is one directory the user chose.
    /// </remarks>
    private const int MaxLocalPathLength = 259;

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
    ///     The number that distinguishes "registered" from "working". A redirection that is installed
    ///     but never hit looks identical, in the settings window and in the log, to one that is doing
    ///     its job — and that is precisely the state the Penumbra route was in for a whole session.
    /// </remarks>
    public int ServedCount => this.served;

    /// <summary>
    ///     Reads the page directory and starts serving it, or explains why it will not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The manifest check is the important part and it is a refusal, not a warning. The
    ///         build-time identity gate proves the pages reproduce the game's own bytes <em>for the
    ///         patch they were built against</em>; nothing checks that at run time. Serving pages
    ///         built against one patch to a client running the next shifts rows, so Spanish lands on
    ///         the wrong row and does it silently. Losing the Spanish until somebody regenerates is
    ///         the better failure by a wide margin.
    ///     </para>
    ///     <para>
    ///         Nothing is hooked until there is something to serve. A plugin that installs a detour on
    ///         a core read path and then redirects nothing is pure risk with no benefit, and this is
    ///         the state every user who never points at a page directory would otherwise be left in.
    ///     </para>
    /// </remarks>
    public static (ExdRedirector? Redirector, string? Error) Create(
        IGameInteropProvider interop, IPluginLog log, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (null, $"No page directory at '{directory}'.");
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

        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tooLong = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.exd", SearchOption.AllDirectories))
        {
            if (file.Length > MaxLocalPathLength)
            {
                tooLong++;
                continue;
            }

            // The folder mirrors the archive, so the game path is the relative path with the
            // separators the game uses.
            pages[Path.GetRelativePath(directory, file).Replace('\\', '/')] = file;
        }

        if (tooLong > 0)
        {
            log.Warning(
                "{Count} page(s) sit at a path longer than {Max} characters and will not be served. "
                + "Move the page directory somewhere shorter.",
                tooLong,
                MaxLocalPathLength);
        }

        if (pages.Count == 0)
        {
            return (null, "The page directory holds no .exd files.");
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
    ///     Every path out calls the original, and the whole body is guarded. This detour sits on the
    ///     one function every file in the game goes through — models, textures, sound, the lot — so
    ///     an exception escaping here would not lose a line of Spanish, it would take the client down
    ///     with it.
    /// </remarks>
    private byte Detour(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync)
    {
        string? local = null;

        try
        {
            if (descriptor is not null && descriptor->ResourceHandle is not null)
            {
                var name = descriptor->ResourceHandle->FileName.AsSpan();

                // The suffix is checked on the raw bytes before anything is allocated. This runs for
                // every file the client reads, and the overwhelming majority are not Excel pages.
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
            // Put the descriptor back the way it came before standing aside — both fields, not just
            // the mode. A passthrough with the mode restored but the scratch pointer still ours sends
            // the game's own read at a stack frame that is about to go away.
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
    ///     The path is written twice, which is not redundancy: the dispatcher's loose-file branch
    ///     reads it out of the scratch buffer, and the descriptor's own <c>FilePath</c> is what the
    ///     rest of the client reports the file as. Writing only one of them loads the right bytes
    ///     under the wrong name, or the wrong bytes under the right one.
    ///     <para>
    ///         The scratch buffer is on the stack, which is sound because the call below completes
    ///         before this frame goes away — the asynchronous reads queue work but copy what they need
    ///         out first, which is the same guarantee Penumbra has relied on for years.
    ///     </para>
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

        // Written through a raw offset rather than the generated field, because the generated field
        // for 0x30 is typed FileInterface* and this is not a FileInterface — calling it one would
        // make the next reader of this line believe something false. See ScratchFieldOffset.
        *(byte**)((byte*)descriptor + ScratchFieldOffset) = scratch;
        descriptor->FileMode = FileMode.LoadUnpackedResource;

        // Logged before the call and again after it, for the first few, and the pair is the point.
        // The first attempt at this took the client down inside the game's read, so the only line
        // that ever reached the log was the one from installing the hook — nothing said which page
        // it died on, or even that it had got as far as trying one. An "attempting" with no
        // "served" beside it answers both.
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
    /// <remarks>
    ///     The same plain-text file <c>Fingerprint.GameVersion</c> reads in CorpusExtractor and
    ///     <c>Manifest</c> stamps from, so both sides of the comparison come from one source.
    /// </remarks>
    private static string? RunningGameVersion()
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
