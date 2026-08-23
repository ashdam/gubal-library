using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.File;

namespace GubalLibrary;

/// <summary>
///     Watches every file the client reads out of its archives, and changes nothing.
/// </summary>
/// <remarks>
///     <para>
///         Answers three questions, all of them still yes and all worth re-asking after a patch: the
///         hook installs on an address the ecosystem maintains, the requested path is readable from
///         the descriptor, and <b>we are in place before the client's boot reads</b>. That last one
///         needs <c>LoadRequiredState: 2</c> in the manifest, and the timestamps here against
///         <c>Boot load started</c> are how it is confirmed.
///     </para>
///     <para>
///         Hooks <c>FileThread.ReadSqPack</c>, which reads out of the archives — deliberately not the
///         <c>DoFileJob</c> that <see cref="ExdRedirector" /> needs, which decides <em>where</em> to
///         read from. A page served from disk therefore stops appearing here, so this checks the
///         redirection too, and neither hook stands on the other.
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

    /// <summary>Reads the path and hands the call straight on.</summary>
    /// <remarks>
    ///     Guarded throughout and the original is called on every path out: a detour that throws takes
    ///     the client with it, and this one exists to gather evidence.
    /// </remarks>
    private byte Detour(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync)
    {
        try
        {
            this.seen++;

            if (descriptor is not null && descriptor->ResourceHandle is not null)
            {
                var path = descriptor->ResourceHandle->FileName.ToString();

                // Only Excel pages. Models, textures and sound are most of the reads and none of
                // this project's business.
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
