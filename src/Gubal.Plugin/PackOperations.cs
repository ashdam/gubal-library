using Dalamud.Plugin.Services;

namespace GubalLibrary;

// Workers perform I/O. Only Framework.Update applies results to configuration and UI state.
internal sealed class PackOperations : IDisposable
{
    private readonly PackInstaller installer;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Queue<Action> completed = new();
    private readonly List<Task> tasks = [];
    private readonly Dictionary<string, PackManifest?> published = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> fetching = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private volatile bool disposed;
    private InstallProgress progress = InstallProgress.Working("Starting");

    public PackOperations(PackInstaller installer, IFramework framework, IPluginLog log)
    {
        this.installer = installer;
        this.framework = framework;
        this.log = log;
        this.HasPending = installer.HasPending;
        this.PendingSource = installer.ReadPendingSource();
        framework.Update += this.ApplyResults;
    }

    public string InstalledPath => this.installer.InstalledPath;
    public bool Installing { get; private set; }
    public bool HasPending { get; private set; }
    public string? PendingSource { get; private set; }
    public string? InstallingSource { get; private set; }
    public InstallProgress Progress { get { lock (this.gate) return this.progress; } }

    public void Install(string source, Configuration config, Action<Configuration> save,
        Action installed, Action<InstallResult> finished)
    {
        if (this.Installing || this.disposed) return;
        this.Installing = true;
        this.InstallingSource = source;
        lock (this.gate) this.progress = InstallProgress.Working("Starting");
        var report = new InstallReporter(value => { lock (this.gate) this.progress = value; });
        this.Run(cancel => this.installer.InstallAsync(source, report, cancel, prepareForRestart: true), result =>
        {
            try
            {
                if (!result.Success)
                    this.log.Error("Could not install the language pack: {Error}", result.Error ?? "Unknown installation error.");
                if (result.Success)
                {
                    this.HasPending = true;
                    this.PendingSource = source;
                    config.PackSource = source;
                    config.ServeLanguagePack = true;
                    save(config);
                    installed();
                }
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
