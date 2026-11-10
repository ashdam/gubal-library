using GubalLibrary;

namespace GubalDesktop;

internal sealed partial class MainWindow
{
    private readonly System.Windows.Forms.Timer storageTimer = new() { Interval = 5000 };
    private Label? downloadSize;
    private Label? installedSize;
    private Label? installedSizeCaption;
    private TableLayoutPanel? packSizes;
    private bool checkingStorage;

    private void AddPackSizes()
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = background, Margin = Padding.Empty, Padding = Padding.Empty };
        Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = muted,
            Margin = new Padding(0, 0, LogicalToDeviceUnits(10), 0) };
        Label Value() => new() { Text = "N/A", AutoSize = true, ForeColor = foreground, Margin = Padding.Empty,
            MinimumSize = new Size(TextRenderer.MeasureText("999.99 GB", Font).Width, 0) };
        downloadSize = Value();
        installedSize = Value();
        installedSizeCaption = Caption(UiText.Get("InstalledSize"));
        panel.Controls.Add(Caption(UiText.Get("DownloadSize")), 0, 0);
        panel.Controls.Add(downloadSize, 1, 0);
        panel.Controls.Add(installedSizeCaption, 0, 1);
        panel.Controls.Add(installedSize, 1, 1);
        languageFooter.Controls.Add(panel);
        panel.Size = panel.GetPreferredSize(Size.Empty);
        panel.Location = new Point(languageAction!.Left - LogicalToDeviceUnits(12) - panel.Width,
            languageAction.Top + Math.Max(0, (languageAction.Height - panel.Height) / 2));
        packSizes = panel;
        if (!storageTimer.Enabled)
        {
            storageTimer.Tick += async (_, _) => await RefreshPackSizesAsync();
            FormClosed += (_, _) => storageTimer.Dispose();
            storageTimer.Start();
        }
        _ = RefreshPackSizesAsync();
    }

    private async Task RefreshPackSizesAsync()
    {
        if (IsDisposed || packSizes is not { IsDisposed: false } panel || !languageFooter.Visible) return;
        var code = languageChoice ?? settings.Language;
        panel.Visible = code is not ("en" or CustomPackCode);
        if (!panel.Visible) return;
        catalog.TryGetValue(code, out var manifest);
        downloadSize!.Text = PackStorage.FormatBytes(manifest?.DownloadBytes);
        installedSize!.Text = PackStorage.FormatBytes(manifest?.InstalledBytes);
        installedSize.ForeColor = foreground;
        installedSizeCaption!.ForeColor = muted;
        tooltips.SetToolTip(installedSize, null);
        tooltips.SetToolTip(installedSizeCaption, null);
        if (checkingStorage || manifest is not { DownloadBytes: > 0, InstalledBytes: > 0 }) return;
        checkingStorage = true;
        try
        {
            var required = PackStorage.RequiredBytes(manifest.DownloadBytes.Value, manifest.InstalledBytes.Value);
            var path = Path.Combine(store.Root, "packs", code, "pack");
            var result = await Task.Run(() => PackStorage.Check(path, required));
            if (IsDisposed || panel.IsDisposed || panel != packSizes || code != (languageChoice ?? settings.Language)) return;
            if (result.Insufficient)
            {
                installedSize.ForeColor = Color.FromArgb(255, 64, 64);
                installedSizeCaption.ForeColor = Color.FromArgb(255, 64, 64);
            }
            var tooltip = result.Error is not null ? UiText.Get("DiskSpaceUnknown") + "\n" + result.Path
                : (result.Insufficient ? UiText.Get("InsufficientDiskSpace") + "\n" : string.Empty)
                    + result.Path + "\n" + UiText.Format("DiskSpaceDetails", PackStorage.FormatBytes(result.RequiredBytes),
                        PackStorage.FormatBytes(result.AvailableBytes));
            tooltips.SetToolTip(installedSize, tooltip);
            tooltips.SetToolTip(installedSizeCaption, tooltip);
        }
        finally { checkingStorage = false; }
    }
}
