using GubalLibrary;

namespace GubalDesktop;

internal sealed partial class MainWindow
{
    private const string CustomPackCode = "custom";
    private string? customPackChoice;

    private string PackActionText(string? code) => UiText.Get(code switch
    {
        "en" => "UseEnglish",
        CustomPackCode => "UseCustomPack",
        _ => "DownloadPrepare",
    });

    private TableLayoutPanel CreateCustomPackFolder()
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = false, Width = 700,
            Margin = new Padding(0, 0, 0, 12) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var path = new TextBox { Text = customPackChoice ?? settings.CustomPackFolder, ReadOnly = true,
            Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AccessibleName = UiText.Get("CustomPackFolder") };
        tooltips.SetToolTip(path, path.Text);
        var browse = new Button { Text = UiText.Get("BrowseFolder"), AutoSize = true };
        Style(browse);
        row.Height = Math.Max(browse.Height + browse.Margin.Vertical, path.PreferredHeight + path.Margin.Vertical);
        row.MinimumSize = new Size(0, row.Height);
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, row.Height));
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = UiText.Get("CustomPackFolder"),
                UseDescriptionForTitle = true, ShowNewFolderButton = false,
                SelectedPath = customPackChoice ?? settings.CustomPackFolder };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            customPackChoice = dialog.SelectedPath;
            languageChoice = CustomPackCode;
            chooseLanguage = true;
            Render();
        };
        row.Controls.Add(path, 0, 0);
        row.Controls.Add(browse, 1, 0);
        return row;
    }

    private async Task SelectCustomPackAsync(CancellationToken cancel)
    {
        var folder = customPackChoice ?? settings.CustomPackFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException(UiText.Get("SelectCustomPackFolder"));
        var installer = new PackInstaller(store, store.Root);
        var result = await Task.Run(() => installer.InstallAsync(folder, cancel: cancel), cancel);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        GameDetection.RequireClosed();
        cancel.ThrowIfCancellationRequested();
        var next = settings with { Language = CustomPackCode, CustomPackFolder = result.Path };
        store.Save(next);
        settings = next;
        customPackChoice = null;
        chooseLanguage = false;
        languageChoice = null;
        disabledParts = [];
        updateStatus = UpdateStatus.NotDeclared;
        SetStatus(UiText.Get("PackReady"));
    }
}
