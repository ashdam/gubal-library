namespace GubalDesktop;

internal sealed partial class MainWindow
{
    private readonly DesktopUpdater desktopUpdater = new();
    private readonly CancellationTokenSource desktopUpdateCancellation = new();
    private bool restartForUpdate;
    private bool desktopUpdatePromptOpen;

    private async Task CheckDesktopUpdateAsync()
    {
        try
        {
            await desktopUpdater.CheckAsync(desktopUpdateCancellation.Token);
            if (IsDisposed) return;
            if (desktopUpdater.AvailableVersion is { } version)
            {
                if (operation is not null || desktopUpdatePromptOpen) return;
                desktopUpdatePromptOpen = true;
                DialogResult answer;
                try
                {
                    answer = MessageBox.Show(this, UiText.Format("DesktopUpdatePrompt", version),
                        "Gubal Desktop", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                }
                finally { desktopUpdatePromptOpen = false; }
                if (answer == DialogResult.Yes) await RunAsync(UpdateDesktopAsync);
            }
        }
        catch (OperationCanceledException) when (desktopUpdateCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            store.Warning(error, "Could not check Desktop updates.");
        }
    }

    private async Task UpdateDesktopAsync(CancellationToken cancel)
    {
        var report = new Progress<int>(percent =>
        {
            if (IsDisposed || operation is null) return;
            DisplayProgress(UiText.Format("DesktopDownloading", percent), Math.Clamp(percent, 0, 100) / 100f);
        });
        await desktopUpdater.DownloadAsync(report, cancel);
        cancel.ThrowIfCancellationRequested();
        desktopUpdater.ApplyAndRestart();
        restartForUpdate = true;
    }
}
