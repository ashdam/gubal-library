using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Asks Penumbra to serve pre-translated <c>.exd</c> pages in place of the game's English ones.
/// </summary>
/// <remarks>
///     <para>
///         The alternative to injection. Instead of intercepting what the game is about to draw and
///         swapping it in a UI node, the game is handed a rebuilt Excel page and draws Spanish
///         through its own text pipeline. That covers surfaces no addon hook reaches, costs nothing
///         per frame, and lets the engine do its own layout.
///     </para>
///     <para>
///         <b>Penumbra does the redirection; this only asks.</b> Its redirection engine is two hooks
///         on byte-pattern signatures — <c>GetResourceSync</c> and <c>ReadSqPack</c> — maintained in
///         Penumbra's own <c>Penumbra.GameData/Signatures.cs</c>, not by Dalamud and not by
///         FFXIVClientStructs. Reimplementing them here would mean inheriting patterns that break
///         whenever Square Enix recompiles the client, where the failure is a crash rather than a
///         line left in English. Going through the published call gate costs a dependency and buys
///         out of that entirely.
///     </para>
///     <para>
///         The call gate label is a public constant in <c>Penumbra.Api</c>
///         (<c>IpcSubscribers/Temporary.cs</c>), used here as a literal rather than by referencing
///         the package, which would be this plugin's first NuGet dependency. The cost of that choice
///         is that a bump to <c>.V6</c> stops working silently, so <see cref="Detect" /> reports the
///         gate as unavailable rather than letting a registration quietly do nothing.
///     </para>
/// </remarks>
internal sealed class PenumbraBridge : IDisposable
{
    /// <summary>Penumbra's internal plugin name, which is what <c>InstalledPlugins</c> matches on.</summary>
    private const string PenumbraInternalName = "Penumbra";

    private const string AddTemporaryModAllLabel = "Penumbra.AddTemporaryModAll.V5";

    private const string RemoveTemporaryModAllLabel = "Penumbra.RemoveTemporaryModAll.V5";

    /// <summary>Shown to the user in Penumbra's own UI as the source of these redirections.</summary>
    private const string Tag = "Gubal Library (ES)";

    /// <summary>
    ///     Zero, deliberately. Nothing else redirects Excel text, so there is nothing to outrank, and
    ///     claiming a high priority would only mask a genuine conflict if one ever appeared.
    /// </summary>
    private const int Priority = 0;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private bool registered;

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    /// <summary>What the settings window shows at the top, recomputed rather than cached.</summary>
    /// <remarks>
    ///     Not cached because the user can install, enable or disable Penumbra while this plugin is
    ///     loaded, and a status line that lies is worse than no status line.
    /// </remarks>
    public PenumbraStatus Detect()
    {
        var penumbra = this.pluginInterface.InstalledPlugins
            .FirstOrDefault(p => string.Equals(p.InternalName, PenumbraInternalName, StringComparison.Ordinal));

        if (penumbra is null)
        {
            return new PenumbraStatus(false, false, null, this.registered);
        }

        return new PenumbraStatus(true, penumbra.IsLoaded, penumbra.Version?.ToString(), this.registered);
    }

    /// <summary>
    ///     Registers every <c>.exd</c> under <paramref name="directory" /> as a redirection.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The manifest check is the important part and it is a refusal, not a warning. The
    ///         build-time identity gate proves the pages reproduce the game's own bytes <em>for the
    ///         patch they were built against</em>; nothing checks that at run time. Serving pages
    ///         built against one patch to a client running the next shifts rows, so text lands on the
    ///         wrong row and does it silently. Losing the Spanish until somebody regenerates is the
    ///         better failure by a wide margin.
    ///     </para>
    /// </remarks>
    public RegistrationResult Register(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return RegistrationResult.Failed($"No page directory at '{directory}'.");
        }

        var status = this.Detect();
        if (!status.Installed)
        {
            return RegistrationResult.Failed("Penumbra is not installed.");
        }

        if (!status.Loaded)
        {
            return RegistrationResult.Failed("Penumbra is installed but not loaded.");
        }

        var manifestPath = Path.Combine(directory, "gubal-manifest.json");
        if (!File.Exists(manifestPath))
        {
            return RegistrationResult.Failed(
                "No gubal-manifest.json in the page directory. Point this at the folder ExdRedirect wrote.");
        }

        string? builtFor;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var root = JsonDocument.Parse(stream).RootElement;
            builtFor = root.TryGetProperty("gameVersion", out var v) ? v.GetString() : null;
        }
        catch (Exception e)
        {
            return RegistrationResult.Failed($"gubal-manifest.json could not be read: {e.Message}");
        }

        var running = RunningGameVersion();
        if (builtFor is null || running is null)
        {
            return RegistrationResult.Failed(
                $"Cannot compare versions (manifest: {builtFor ?? "none"}, game: {running ?? "unknown"}).");
        }

        if (!string.Equals(builtFor, running, StringComparison.Ordinal))
        {
            return RegistrationResult.Failed(
                $"These pages were built for game {builtFor} but the client is running {running}. "
                + "Regenerate them; serving them now would put text on the wrong rows.");
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.exd", SearchOption.AllDirectories))
        {
            // The folder mirrors the archive, so the game path is just the relative path with the
            // separators the game uses.
            var gamePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            paths[gamePath] = file;
        }

        if (paths.Count == 0)
        {
            return RegistrationResult.Failed("The page directory holds no .exd files.");
        }

        try
        {
            var add = this.pluginInterface
                .GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>(AddTemporaryModAllLabel);

            var code = add.InvokeFunc(Tag, paths, string.Empty, Priority);
            if (code != 0)
            {
                return RegistrationResult.Failed($"Penumbra refused the redirections (error code {code}).");
            }
        }
        catch (Exception e)
        {
            // Most likely the call gate is gone or renamed — Penumbra bumping the label past .V5.
            return RegistrationResult.Failed($"{AddTemporaryModAllLabel} could not be called: {e.Message}");
        }

        this.registered = true;
        this.log.Information(
            "Registered {Count} page redirection(s) with Penumbra for game {Version}.", paths.Count, builtFor);

        return RegistrationResult.Succeeded(paths.Count, builtFor);
    }

    public void Unregister()
    {
        if (!this.registered)
        {
            return;
        }

        try
        {
            var remove = this.pluginInterface.GetIpcSubscriber<string, int, int>(RemoveTemporaryModAllLabel);
            remove.InvokeFunc(Tag, Priority);
        }
        catch (Exception e)
        {
            this.log.Warning("Could not remove the Penumbra redirections: {Message}", e.Message);
        }

        this.registered = false;
    }

    public void Dispose() => this.Unregister();

    /// <summary>
    ///     The patch the client is running, from <c>ffxivgame.ver</c> beside the executable.
    /// </summary>
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

/// <param name="Installed">Penumbra is present in the plugin list.</param>
/// <param name="Loaded">And actually running, which is what makes the call gate answer.</param>
/// <param name="Version">Its version, for the settings window.</param>
/// <param name="Registered">Whether this plugin currently has redirections in place.</param>
internal readonly record struct PenumbraStatus(bool Installed, bool Loaded, string? Version, bool Registered);

internal readonly record struct RegistrationResult(bool Success, int PageCount, string? GameVersion, string? Error)
{
    public static RegistrationResult Succeeded(int pages, string gameVersion) => new(true, pages, gameVersion, null);

    public static RegistrationResult Failed(string error) => new(false, 0, null, error);
}
