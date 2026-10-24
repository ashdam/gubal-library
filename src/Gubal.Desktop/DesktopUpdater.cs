using Velopack;
using Velopack.Sources;

namespace GubalDesktop;

internal sealed class DesktopUpdater
{
    private const string Channel = "desktop-win";
    private readonly UpdateManager manager;
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateInfo? available;

    public DesktopUpdater()
    {
        var source = Environment.GetEnvironmentVariable("GUBAL_DESKTOP_UPDATE_SOURCE")
            ?? AppContext.GetData("Gubal.Desktop.UpdateSource") as string;
        var options = new UpdateOptions { ExplicitChannel = Channel };
        manager = string.IsNullOrWhiteSpace(source)
            ? new UpdateManager(new GithubSource("https://github.com/ashdam/gubal-library", null, false), options)
            : new UpdateManager(source, options);
    }

    public bool IsPackaged => manager.IsInstalled;
    public string? AvailableVersion => (manager.UpdatePendingRestart ?? available?.TargetFullRelease)?.Version is { } version
        ? DisplayVersion(version) : null;
    public bool IsReady => manager.UpdatePendingRestart is not null;

    // Package revisions preserve the order of the four-part application version.
    private static string DisplayVersion(SemanticVersion version) => version.ReleaseLabels.ToArray() is ["rev", var revision]
        && int.TryParse(revision, out var number) ? $"{version.Major}.{version.Minor}.{version.Patch}.{number}" : version.ToString();

    public async Task CheckAsync(CancellationToken cancel)
    {
        if (!IsPackaged) return;
        await gate.WaitAsync(cancel);
        try
        {
            cancel.ThrowIfCancellationRequested();
            available = await manager.CheckForUpdatesAsync();
            cancel.ThrowIfCancellationRequested();
        }
        finally { gate.Release(); }
    }

    public async Task DownloadAsync(IProgress<int> progress, CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            if (IsReady) return;
            var update = available ?? throw new InvalidOperationException("No Desktop update is available.");
            await manager.DownloadUpdatesAsync(update, progress.Report, cancel);
        }
        finally { gate.Release(); }
    }

    public void ApplyAndRestart()
    {
        GameDetection.RequireClosed();
        var update = manager.UpdatePendingRestart ?? throw new InvalidOperationException("No Desktop update is ready.");
        manager.WaitExitThenApplyUpdates(update, silent: true, restart: true);
    }
}
