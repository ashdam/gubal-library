using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.File;

namespace GubalLibrary;

/// <summary>
///     Watches every file the client reads out of its archives, and changes nothing.
/// </summary>
/// <remarks>
///     <para>
///         Step one of finding out whether this plugin could redirect files itself instead of asking
///         Penumbra to. It hooks the read and calls the original untouched, so it cannot produce a
///         false positive — nothing it does can put Spanish on screen. All three answers came back
///         yes, and <see cref="ExdRedirector" /> is what was built on them; this stays as the way to
///         check that they are still yes after a patch. What it answers:
///     </para>
///     <list type="number">
///         <item>the hook installs on an address the ecosystem maintains, and survives;</item>
///         <item>the requested path is readable from the descriptor;</item>
///         <item><b>we are in place before the client's boot reads</b>, which is the one that matters.</item>
///     </list>
///     <para>
///         Nothing here comes from Penumbra. <c>FileThread.Addresses.ReadSqPack</c> and the
///         <c>FileDescriptor</c> / <c>ResourceHandle</c> layouts are all FFXIVClientStructs, which is
///         MIT, ships with Dalamud and is updated by the whole community within hours of a patch.
///         That is what makes this worth considering at all: the fragile part — locating a function
///         in a recompiled client — is somebody else's maintained problem rather than ours.
///     </para>
///     <para>
///         Deliberately a different function from the one <see cref="ExdRedirector" /> hooks. This is
///         <c>FileThread.ReadSqPack</c>, which reads out of the archives; the redirection has to go on
///         <c>FileThread.DoFileJob</c>, which is the one that decides <em>where</em> to read from. So
///         a page being served from disk stops appearing here — which makes this a check on the
///         redirection as well as on the timing, without either hook standing on the other.
///     </para>
///     <para>
///         The third question is the interesting one and it was learned the hard way. Dalamud loads
///         plugins in stages and only holds the game's boot for the first; a plugin that does not
///         declare <c>LoadRequiredState: 2</c> lands in the last stage, seconds after the client has
///         already read its sheets. This plugin now declares it, and the timestamps below against
///         <c>Boot load started</c> are how we confirm it took effect.
///     </para>
/// </remarks>
internal sealed unsafe class SqPackProbe : IDisposable
{
    private readonly IPluginLog log;
    private readonly Hook<ReadSqPackDelegate>? hook;

    private int seen;
    private int excel;

    public SqPackProbe(IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;

        try
        {
            this.hook = interop.HookFromAddress<ReadSqPackDelegate>(
                FileThread.Addresses.ReadSqPack.Value,
                this.Detour);

            this.hook.Enable();
            log.Information("SqPack probe attached at 0x{Address:X}.", FileThread.Addresses.ReadSqPack.Value);
        }
        catch (Exception e)
        {
            log.Error(e, "SqPack probe could not attach.");
        }
    }

    private delegate byte ReadSqPackDelegate(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync);

    public int Seen => this.seen;

    public int Excel => this.excel;

    public void Dispose()
    {
        this.hook?.Disable();
        this.hook?.Dispose();
    }

    /// <summary>
    ///     Reads the path and hands the call straight on.
    /// </summary>
    /// <remarks>
    ///     Everything is inside a try/catch and the original is called on every path out. A detour
    ///     that throws takes the client with it, and this one exists to gather evidence, not to be
    ///     the first thing that ever crashed a tester.
    /// </remarks>
    private byte Detour(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync)
    {
        try
        {
            this.seen++;

            if (descriptor is not null && descriptor->ResourceHandle is not null)
            {
                var path = descriptor->ResourceHandle->FileName.ToString();

                // Only Excel pages are logged. Everything else — models, textures, sound — is the
                // overwhelming majority of reads and none of this project's business.
                if (path.EndsWith(".exd", StringComparison.OrdinalIgnoreCase))
                {
                    this.excel++;
                    this.log.Information("[probe] {Sync} {Path}", isSync ? "sync " : "async", path);
                }
            }
        }
        catch
        {
            // Swallowed on purpose: a probe must never be the reason a read fails.
        }

        return this.hook!.Original(thread, descriptor, priority, isSync);
    }
}
