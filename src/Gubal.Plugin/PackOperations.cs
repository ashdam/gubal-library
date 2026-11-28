using CheapLoc;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

// Workers perform I/O. Only Framework.Update applies results to configuration and UI state.
internal sealed class PackOperations : IDisposable
{
    private readonly PackInstaller installer;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly string gameDataRoot;
    private readonly string shadowFolder;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Queue<Action> completed = new();
    private readonly List<Task> tasks = [];
    private readonly Dictionary<string, PackManifest?> published = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> fetching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, (DateTime Checked, PackStorageStatus Status)> storage = [];
    private readonly HashSet<long> checkingStorage = [];
    private readonly object gate = new();
    private volatile bool disposed;
    private InstallProgress progress = InstallProgress.Working("Starting");
    private InstallResult? automaticUpdateSpaceFailure;

    public PackOperations(PackInstaller installer, IFramework framework, IPluginLog log, string gameDataRoot, string shadowFolder)
    {
        this.installer = installer;
        this.framework = framework;
        this.log = log;
        this.gameDataRoot = gameDataRoot;
        this.shadowFolder = shadowFolder;
        this.HasPending = installer.HasPending;
        this.PendingSource = installer.ReadPendingSource();
        framework.Update += this.ApplyResults;
    }

    public string InstalledPath => this.installer.InstalledPath;
    public bool Installing { get; private set; }
    public bool HasPending { get; private set; }
    public bool AutomaticUpdateNeedsSpace => this.automaticUpdateSpaceFailure is not null;
    public string? PendingSource { get; private set; }
    public string? InstallingSource { get; private set; }
    public InstallProgress Progress { get { lock (this.gate) return this.progress; } }
    public string? UpdateSpaceNotice => this.automaticUpdateSpaceFailure switch
    {
        null => null,
        { DiskSpace: { AvailableBytes: { } available } space } => string.Format(
            Loc.Localize("Update.InsufficientDiskSpaceDetails",
                "Could not install the language pack update. Not enough free disk space. Required: {0}. Available: {1}."),
            PackStorage.FormatBytes(space.RequiredBytes), PackStorage.FormatBytes(available)),
        _ => Loc.Localize("Update.InsufficientDiskSpace", "Could not install the language pack update. Not enough free disk space."),
    };
    public string PendingRestartNotice
    {
        get
        {
            var name = KnownPacks.All.FirstOrDefault(pack => pack.Source is not null
                && string.Equals(pack.Source, this.PendingSource, StringComparison.OrdinalIgnoreCase)).Name;
            return name is not null
                ? string.Format(Loc.Localize("Restart.PackReady", "The {0} language pack is ready. Restart FFXIV to apply it."), name)
                : Loc.Localize("Restart.LocalPackReady", "The language pack is ready. Restart FFXIV to apply it.");
        }
    }

    public void Install(string source, Configuration config, Action<Configuration> save,
        Action notice, Action<InstallResult> finished, PackManifest? automaticUpdate = null)
    {
        if (this.Installing || this.disposed) return;
        this.Installing = true;
        this.InstallingSource = source;
        this.automaticUpdateSpaceFailure = null;
        lock (this.gate) this.progress = InstallProgress.Working("Starting");
        var report = new InstallReporter(value => { lock (this.gate) this.progress = value; });
        var disabledSheets = new HashSet<string>(config.DisabledSheets, config.DisabledSheets.Comparer);
        var shadowLabel = Loc.Localize("Install.PreparingShadow", "Preparing shared translation");
        var pack = KnownPacks.All.FirstOrDefault(pack => string.Equals(pack.Source, source, StringComparison.OrdinalIgnoreCase));
        this.published.TryGetValue(pack.Code ?? string.Empty, out var manifest);
        this.Run(cancel => this.installer.InstallAsync(source, report, cancel, prepareForRestart: true,
            expectedManifest: automaticUpdate ?? manifest, automaticUpdate: automaticUpdate is not null,
            preparePending: (working, destination, packManifest, token) =>
            {
                report.Report(InstallProgress.Working(shadowLabel));
                GubalLumina.Prepare(this.gameDataRoot, this.shadowFolder, working, destination, packManifest,
                    disabledSheets, this.log, token);
            }), result =>
        {
            try
            {
                if (!result.Success && !result.FailureLogged)
                    this.log.Error("Could not install the language pack: {Error}", result.Error ?? "Unknown installation error.");
                if (result.Success)
                {
                    this.HasPending = true;
                    this.PendingSource = source;
                    if (automaticUpdate is null)
                    {
                        config.PackSource = source;
                        config.ServeLanguagePack = true;
                        save(config);
                    }
                }
                if (automaticUpdate is not null && result.InsufficientSpace)
                    this.automaticUpdateSpaceFailure = result;
                if (result.Success || this.AutomaticUpdateNeedsSpace)
                    notice();
                finished(result);
            }
            catch (Exception error)
            {
                this.log.Error(error, "Could not apply the pack installation result.");
                finished(InstallResult.Failed(error.Message));
            }
            finally
            {
                this.Installing = false;
                this.InstallingSource = null;
            }
        }, InstallResult.Failed("The pack installation failed. See the plugin log."));
    }

    public void Check(PackManifest? manifest, string? gameVersion, Action<UpdateStatus> finished) =>
        this.Run(cancel => this.installer.CheckForUpdateAsync(manifest, cancel, gameVersion), finished,
            UpdateStatus.Unreachable("The update check failed. See the plugin log."));

    public (PackManifest? Manifest, bool Loading) Published(KnownPack pack)
    {
        if (this.published.TryGetValue(pack.Code, out var manifest)) return (manifest, false);
        if (pack.ManifestUrl is not { } url || this.disposed) return (null, false);
        if (this.fetching.Add(pack.Code))
            this.Run(cancel => this.installer.FetchManifestAsync(url, cancel),
                result => this.published[pack.Code] = result, (PackManifest?)null);
        return (null, true);
    }

    public PackStorageStatus? Storage(PackManifest? manifest)
    {
        if (manifest is not { DownloadBytes: > 0, InstalledBytes: > 0 }) return null;
        var required = PackStorage.RequiredBytes(manifest.DownloadBytes.Value, manifest.InstalledBytes.Value);
        var found = this.storage.TryGetValue(required, out var cached);
        if ((!found || DateTime.UtcNow - cached.Checked > TimeSpan.FromSeconds(5)) && this.checkingStorage.Add(required))
            this.Run(_ => Task.FromResult(PackStorage.Check(this.InstalledPath, required)), result =>
            {
                this.storage[required] = (DateTime.UtcNow, result);
                this.checkingStorage.Remove(required);
            }, new PackStorageStatus(this.InstalledPath, required, null, "Could not check free disk space."));
        return found ? cached.Status : null;
    }

    private void Run<T>(Func<CancellationToken, Task<T>> work, Action<T> apply, T failure)
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            var token = this.cancellation.Token;
            this.tasks.RemoveAll(task => task.IsCompleted);
            this.tasks.Add(Task.Run(async () =>
            {
                var result = failure;
                try { result = await work(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) { this.log.Error(error, "A pack operation failed."); }
                lock (this.gate)
                {
                    if (!this.disposed) this.completed.Enqueue(() => apply(result));
                }
            }));
        }
    }

    private void ApplyResults(IFramework _)
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            while (this.completed.TryDequeue(out var apply))
            {
                try { apply(); }
                catch (Exception error) { this.log.Error(error, "Could not apply a pack operation result."); }
            }
        }
    }

    public void Dispose()
    {
        Task[] pending;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            this.completed.Clear();
            pending = this.tasks.ToArray();
        }
        this.framework.Update -= this.ApplyResults;
        this.cancellation.Cancel();
        _ = Task.WhenAll(pending).ContinueWith(_ => this.cancellation.Dispose(), TaskScheduler.Default);
    }

    private sealed class InstallReporter(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
