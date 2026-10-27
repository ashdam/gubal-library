using System.Diagnostics;
using System.Text.Json;
using GubalLibrary;

namespace GubalDesktop;

internal sealed partial class MainWindow : Form
{
    private readonly DesktopStore store = new();
    private readonly DesktopUsage usage;
    private readonly InstallationFooter installationFooter;
    private DesktopSettings settings = new();
    private readonly Color background = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(20, 22, 24);
    private readonly Color foreground = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(237, 240, 242);
    private readonly Color gold = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(237, 200, 125);
    private readonly Color muted = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(166, 173, 181);
    private readonly Color errorColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(255, 130, 130);
    private readonly FlowLayoutPanel content = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(28, 20, 28, 16) };
    private readonly StatusLabel status = new() { AutoSize = false, Height = 48, Dock = DockStyle.Bottom, Padding = new Padding(28, 4, 28, 4) };
    private readonly Label launchNotice = new() { Text = UiText.Get("KeepWindowOpen"), Dock = DockStyle.Bottom,
        Height = 64, Padding = new Padding(28, 8, 28, 8), TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 5, Visible = false };
    private readonly Panel languageFooter = new() { Dock = DockStyle.Bottom, Visible = false, Padding = new Padding(28, 12, 28, 16) };
    private readonly Button setup = new() { Text = UiText.Get("Settings"), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(16, 0, 0, 0) };
    private CancellationTokenSource? operation;
    private bool launching;
    private bool chooseLanguage;
    private string? languageChoice;
    private bool launcherOpened;
    private bool translationLoaded;
    private UpdateStatus? updateStatus;
    private string[] disabledParts = [];
    private string componentError = "";
    private TranslationStartupException? startupFailure;
    private Button? startButton;
    private KnownPack Selected => KnownPacks.All.FirstOrDefault(pack => pack.Code == settings.Language);
    private PackInstaller Installer => new(store, store.LanguageFolder(settings.Language));
    private PackManifest? Manifest => KnownPacks.ForCode(settings.Language) is null ? null : PackManifest.Read(Installer.InstalledPath).Manifest;
    private static string EngineFolder => Path.Combine(AppContext.BaseDirectory, "engine");

    public MainWindow()
    {
        usage = new DesktopUsage(store);
        var version = typeof(MainWindow).Assembly.GetName().Version;
        Text = $"Gubal Library ({version}) · Desktop · Preview / Alpha";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = background;
        ForeColor = foreground;
        ClientSize = new Size(820, 740);
        MinimumSize = new Size(610, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(content);
        Controls.Add(progress);
        Controls.Add(status);
        Controls.Add(launchNotice);
        Controls.Add(languageFooter);
        installationFooter = CreateInstallationFooter();
        Controls.Add(installationFooter);
        status.ForeColor = muted;
        launchNotice.ForeColor = gold;
        Style(setup);
        setup.Location = new Point(ClientSize.Width - content.Padding.Right - setup.Width, content.Padding.Top);
        Controls.Add(setup);
        setup.BringToFront();
        setup.Click += (_, _) => SettingsDialog();
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) =>
        {
            if (operation is null) return;
            e.Cancel = true;
            if (launching) SetStatus(UiText.Get("FinishLogin"));
            else { operation.Cancel(); SetStatus(UiText.Get("Cancelling")); }
        };
        content.SizeChanged += (_, _) => ResizeContent();
        FormClosed += (_, _) => { usage.Dispose(); desktopUpdateCancellation.Cancel(); desktopUpdateCancellation.Dispose(); catalogCancellation.Cancel(); catalogCancellation.Dispose(); tooltips.Dispose(); reviewerImage?.Dispose(); foreach (var image in flags.Values) image.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            settings = store.Read();
            if (settings.Language != "en" && !KnownPacks.All.Any(pack => pack.Code == settings.Language)) settings = settings with { Language = "" };
            if (GameDetection.Normalize(settings.GameFolder) is null || settings.Account is null)
            {
                SetStatus(UiText.Get("FindingGame"));
                var found = await Task.Run(GameDetection.Find);
                if (IsDisposed) return;
                using var form = CreateSettingsDialog(found);
                form.ShowDialog(this);
            }
            store.Save(settings);
            usage.Flush();
        }
        catch (Exception error) { if (!IsDisposed) ShowError(error); }
        if (!IsDisposed)
        {
            Render();
            FitInitialWindow();
            await CheckDesktopUpdateAsync();
            if (IsDisposed) return;
            if (settings.Language != "en" && Manifest is not null)
                await RunAsync(CheckPackUpdateAsync);
            await LoadCatalogAsync();
        }
    }

    private void Render()
    {
        installationFooter.SetFolder(settings.GameFolder);
        content.SuspendLayout();
        content.AutoScrollPosition = Point.Empty;
        tooltips.RemoveAll();
        foreach (var old in content.Controls.Cast<Control>().ToArray()) old.Dispose();
        content.Controls.Clear();
        foreach (var old in languageFooter.Controls.Cast<Control>().ToArray()) old.Dispose();
        languageFooter.Controls.Clear();
        languageFooter.Visible = false;
        languageAction = null;
        startButton = null;
        if (startupFailure is not null)
        {
            AddHeading(UiText.Get("NativeIncompatibleTitle"));
            AddText(UiText.Get("NativeStartupFailed"), errorColor, 15);
            AddText(UiText.Format("ClientVersions", typeof(MainWindow).Assembly.GetName().Version, GameVersion()), muted);
            AddButton(UiText.Get("OpenDetails"), () =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { store.Root }, UseShellExecute = true });
                return Task.CompletedTask;
            });
            AddButton(UiText.Get("Close"), () => { Close(); return Task.CompletedTask; });
            content.ResumeLayout(); ResizeContent(); return;
        }
        var manifest = Manifest;
        if (launcherOpened)
        {
            AddHeading(UiText.Get("ContinueLauncher"));
            AddText(UiText.Get("PreparationDone"), muted);
            AddSeparator();
            AddText(translationLoaded ? (manifest?.LanguageName ?? Selected.Name) + UiText.Get("TranslationLoaded") : UiText.Get("EnglishLoaded"), Color.LightGreen, 16);
            AddText(UiText.Get("CanClose"), muted);
            AddButton(UiText.Get("Back"), () => { launcherOpened = false; Render(); return Task.CompletedTask; });
            content.ResumeLayout(); ResizeContent(); return;
        }
        var pendingSelection = languageChoice is not null && languageChoice != settings.Language;
        var showChoices = chooseLanguage || manifest is null && settings.Language != "en" && !pendingSelection;
        AddHeading(showChoices ? UiText.Get("Welcome") : $"Gubal Library - {typeof(MainWindow).Assembly.GetName().Version}");
        AddText(UiText.Get("PreviewDisclaimer"), SystemInformation.HighContrast ? foreground : Color.FromArgb(120, 190, 255));
        if (showChoices) AddText(UiText.Get("ChooseLanguage"), muted);
        if (GameDetection.Normalize(settings.GameFolder) is null)
        {
            AddText(UiText.Get("ChooseGameFolder"), gold);
            AddButton(UiText.Get("BrowseGame"), () => { BrowseGame(); return Task.CompletedTask; });
        }
        if (showChoices)
        {
            AddLanguageChoices();
            AddAutoUpdate();
            AddLanguageActions(showBack: manifest is not null || settings.Language == "en");
        }
        else if (pendingSelection)
        {
            AddSeparator();
            AddSelectedLanguage(null, languageChoice);
            if (languageChoice != "en")
            {
                AddText(UiText.Get("PackNotInstalled"), muted);
                AddAutoUpdate();
            }
            else AddText(UiText.Get("EnglishNoDownload"), muted);
            AddLanguageActions();
        }
        else
        {
            AddSeparator();
            AddSelectedLanguage(manifest);
            AddSeparator();
            if (settings.Language == "en") AddText(UiText.Get("EnglishNoDownload"), muted);
            else if (disabledParts.Length > 0)
            {
                AddText(UiText.Get("GameUpdated"), gold, 15);
                AddText(UiText.Get("CompatibilityParts") + string.Join("\n", disabledParts.Select(part => "• " + part)), muted);
            }
            else if (updateStatus?.State == UpdateState.Available) AddText(UiText.Get("NewPack"), gold, 15);
            else if (updateStatus?.State == UpdateState.Unreachable) AddText(UiText.Get("UpdateOffline"), gold);
            if (settings.Language != "en") AddAutoUpdate();
            startButton = AddButton(settings.Language == "en" ? UiText.Get("StartOriginal") : updateStatus?.State == UpdateState.Available && settings.AutoUpdate
                ? UiText.Get("UpdateStart") : UiText.Get("StartGame"), () => RunAsync(StartAsync, showLaunchNotice: true), true);
        }
        content.ResumeLayout();
        ResizeContent();
        if (showChoices) FitInitialWindow();
        content.AutoScrollPosition = Point.Empty;
        if (operation is null) SetStatus("");
    }

    private void AddAutoUpdate()
    {
        var check = new CheckBox { Text = UiText.Get("AutoUpdate"), Checked = settings.AutoUpdate, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        check.CheckedChanged += (_, _) => { settings = settings with { AutoUpdate = check.Checked }; store.Save(settings); Render(); };
        content.Controls.Add(check);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool showLaunchNotice = false)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        launchNotice.Visible = showLaunchNotice;
        content.Enabled = false;
        languageFooter.Enabled = false;
        setup.Enabled = false;
        progress.Visible = true;
        progress.Style = ProgressBarStyle.Marquee;
        var cancelButton = new Button { Text = UiText.Get("Cancel"), Dock = DockStyle.Bottom, AutoSize = true };
        Style(cancelButton);
        cancelButton.Click += (_, _) => { if (!launching) cancellation.Cancel(); };
        Controls.Add(cancelButton);
        cancelButton.BringToFront();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus(UiText.Get("Cancelled")); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            operation = null;
            launching = false;
            launchNotice.Visible = false;
            cancelButton.Dispose();
            progress.Visible = false;
            content.Enabled = true;
            languageFooter.Enabled = true;
            setup.Enabled = true;
            var message = status.Text;
            Render();
            SetStatus(message);
            if (restartForUpdate) Close();
        }
    }

    private async Task InstallAsync(CancellationToken cancel)
    {
        GameDetection.RequireClosed();
        if (GameDetection.Normalize(settings.GameFolder) is null) throw new InvalidOperationException(UiText.Get("SelectFolderFirst"));
        var selectedLanguage = languageChoice ?? settings.Language;
        if (selectedLanguage == "en")
        {
            var next = settings with { Language = "en" };
            store.Save(next);
            settings = next;
            chooseLanguage = false; languageChoice = null; disabledParts = []; updateStatus = null;
            SetStatus(UiText.Get("EnglishSelected"));
            return;
        }
        var selectedPack = KnownPacks.All.Single(pack => pack.Code == selectedLanguage);
        if (selectedPack.Source is not { } source) throw new InvalidOperationException(UiText.Get("Unpublished"));
        var progressClock = Stopwatch.StartNew();
        string? lastPhase = null;
        var report = new Progress<InstallProgress>(value =>
        {
            if (IsDisposed || operation is null) return;
            if (value.Label == lastPhase && value.Fraction != 1 && progressClock.ElapsedMilliseconds < 100) return;
            lastPhase = value.Label;
            progressClock.Restart();
            var phase = value.Label switch
            {
                "Downloading" => UiText.Get("Downloading"), "Connecting" => UiText.Get("Connecting"), "Unpacking" => UiText.Get("Unpacking"),
                "Checking the pack" => UiText.Get("CheckingPack"), _ => UiText.Get("PreparingPack"),
            };
            DisplayProgress($"{phase} · {value.Detail}", value.Fraction);
        });
        var installer = new PackInstaller(store, store.LanguageFolder(selectedLanguage));
        var result = await Task.Run(() => installer.InstallAsync(source, report, cancel, prepareForRestart: true), cancel);
        if (!result.Success)
        {
            cancel.ThrowIfCancellationRequested();
            throw new InvalidOperationException(result.Error);
        }
        // Recheck before activation. Never replace a pack while the game is open.
        GameDetection.RequireClosed();
        cancel.ThrowIfCancellationRequested();
        var activation = await Task.Run(() => installer.ActivatePending((_, _) => store.Save(settings with { Language = selectedLanguage })), cancel);
        if (activation is not { Success: true }) throw new InvalidOperationException(activation?.Error ?? UiText.Get("ActivationFailed"));
        settings = settings with { Language = selectedLanguage };
        chooseLanguage = false;
        languageChoice = null;
        disabledParts = [];
        updateStatus = UpdateStatus.UpToDate;
        SetStatus(UiText.Get("PackReady"));
    }

    private async Task UpdateAsync(CancellationToken cancel, bool offer)
    {
        await CheckPackUpdateAsync(cancel);
        var update = updateStatus!.Value;
        if (update.State == UpdateState.Available)
        {
            if (!offer || MessageBox.Show(this, UiText.Format("NewPackPrompt", update.Published?.TranslationVersion), UiText.Get("UpdateTitle"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                await InstallAsync(cancel);
                updateStatus = UpdateStatus.UpToDate;
            }
        }
    }

    private async Task CheckPackUpdateAsync(CancellationToken cancel)
    {
        SetStatus(UiText.Get("CheckingUpdates"));
        var update = await Installer.CheckForUpdateAsync(Manifest, cancel, GameVersion());
        updateStatus = update;
        if (update.State == UpdateState.Unreachable)
        {
            store.Warning(update.Error ?? "Update check failed.");
            SetStatus(UiText.Get("UpdateOffline"));
        }
        else SetStatus("");
    }

    private async Task StartAsync(CancellationToken cancel)
    {
        store.Outbox.Errors.BeginSession();
        try { await StartGameAsync(cancel); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await usage.RecordAsync(UsageState(active: false, compatible: error is not TranslationStartupException), send: false);
            throw;
        }
    }

    private UsageSnapshot UsageState(bool active, bool compatible)
    {
        var manifest = Manifest;
        return UsageSnapshot.Create(settings.Language == "en" ? null : settings.Language.ToLowerInvariant(),
            manifest?.TranslationVersion, settings.Language != "en", manifest is not null,
            enabled: true, active, compatible);
    }

    private async Task StartGameAsync(CancellationToken cancel)
    {
        GameDetection.RequireClosed();
        if (settings.Account is null)
        {
            using var form = CreateSettingsDialog(await Task.Run(GameDetection.Find, cancel));
            if (form.ShowDialog(this) != DialogResult.OK) return;
        }
        var game = GameDetection.Normalize(settings.GameFolder) ?? throw new InvalidOperationException(UiText.Get("SelectFolder"));
        if (settings.Language == "en")
        {
            await StartOriginalAsync(game, compatible: true);
            return;
        }
        var installer = Installer;
        if (installer.HasPending)
        {
            var result = installer.ActivatePending((_, _) => store.Save(settings));
            if (result is not { Success: true }) throw new InvalidOperationException(result?.Error ?? UiText.Get("PendingFailed"));
        }
        if (settings.AutoUpdate)
        {
            try { await UpdateAsync(cancel, false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                store.Error(error, "Could not update the pack before launch.");
                if (MessageBox.Show(this, UiText.Get("UpdateFailedPrompt"), UiText.Get("UpdateUnavailable"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            }
        }
        var version = GameVersion();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException(UiText.Get("GameVersionUnknown"));
        var selection = new
        {
            GameExecutable = Path.Combine(game, "game", "ffxiv_dx11.exe"),
            Launcher = Path.Combine(game, "boot", "ffxivboot64.exe"),
            Pack = installer.InstalledPath, ExpectedGameVersion = version,
            Account = settings.Account,
            Mode = "redirect", Scope = "all-resources",
        };
        var launchSettings = Path.Combine(store.Root, "launch.json");
        File.WriteAllText(launchSettings, JsonSerializer.Serialize(selection));
        SetStatus(UiText.Get("CheckingCompatibility"));
        var check = await RunComponentAsync("GubalStandalone.Runtime.exe", ["--check", launchSettings], cancel);
        if (check == 3)
            throw new TranslationStartupException(componentError);
        if (check != 0)
            throw new InvalidOperationException(UiText.Get("PackCannotActivate"));
        if (await RunComponentAsync("GubalStandalone.Launcher.exe", ["--check-host", launchSettings, EngineFolder], cancel) != 0)
            throw new TranslationStartupException(componentError);
        var reportPath = Path.ChangeExtension(launchSettings, ".compatibility.json");
        if (File.Exists(reportPath))
        {
            var report = JsonSerializer.Deserialize<CompatibilityReport>(File.ReadAllText(reportPath));
            disabledParts = report?.DisabledParts ?? [];
            if (disabledParts.Length > 0 && MessageBox.Show(this,
                UiText.Get("CompatibilityPrompt") + string.Join("\n", disabledParts) + UiText.Get("ContinuePrompt"),
                UiText.Get("CompatibilityTitle"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        }
        cancel.ThrowIfCancellationRequested();
        GameDetection.RequireClosed();
        launching = true;
        SetStatus(UiText.Get("LoginPrompt"));
        var launch = await RunComponentAsync("GubalStandalone.Launcher.exe", ["--launch", launchSettings, EngineFolder], CancellationToken.None);
        if (launch == 3) throw new TranslationStartupException(componentError);
        if (launch != 0)
            throw new InvalidOperationException(UiText.Get("LaunchFailed") + componentError);
        launcherOpened = true;
        translationLoaded = true;
        SetStatus(UiText.Get("GameLoaded"));
        await usage.RecordAsync(UsageState(active: true, compatible: true));
    }

    private async Task StartOriginalAsync(string game, bool compatible)
    {
        Process.Start((settings.Account ?? new()).StartInfo(Path.Combine(game, "boot", "ffxivboot64.exe")));
        launcherOpened = true;
        translationLoaded = false;
        SetStatus(UiText.Get("OriginalLauncherOpen"));
        await usage.RecordAsync(UsageState(active: false, compatible));
    }

    private async Task<int> RunComponentAsync(string name, string[] arguments, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(Path.Combine(EngineFolder, name))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = store.Root,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(UiText.Get("ComponentFailed"));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancel); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); throw; }
        var standardOutput = await output;
        var standardErrors = await errors;
        store.Information(standardOutput);
        store.Warning(standardErrors);
        componentError = string.Join(Environment.NewLine, (standardErrors.Length > 0 ? standardErrors : standardOutput)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
        if (process.ExitCode != 0)
            store.Outbox.Errors.Capture(arguments.Contains("--launch") ? "launch" : arguments.Contains("--check-host") ? "host-check" : "pack-check",
                "component-failed", componentError, exitCode: process.ExitCode);
        return process.ExitCode;
    }

    private string? GameVersion()
    {
        var file = Path.Combine(settings.GameFolder, "game", "ffxivgame.ver");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    private void SettingsDialog()
    {
        if (operation is not null) return;
        using var form = CreateSettingsDialog();
        if (form.ShowDialog(this) == DialogResult.OK) Render();
    }

    private Form CreateSettingsDialog(string[]? installations = null)
    {
        installations ??= GameDetection.Find();
        var form = new Form { Text = UiText.Get("SettingsTitle"), ClientSize = new Size(570, 180), MinimumSize = new Size(480, 220), AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent, BackColor = background, ForeColor = foreground, Font = Font, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var label = new Label { Text = UiText.Get("GameFolder"), AutoSize = true };
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 2);
        var initialPath = GameDetection.Normalize(settings.GameFolder) ?? installations?.FirstOrDefault() ?? "";
        var path = new TextBox { Text = initialPath, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        layout.Controls.Add(path, 0, 2);
        var browse = new Button { Text = UiText.Get("BrowseFolder"), AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(12, 3, 3, 3) };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = UiText.Get("FolderPrompt"), UseDescriptionForTitle = true, SelectedPath = path.Text, ShowNewFolderButton = false }; if (picker.ShowDialog(form) == DialogResult.OK) { if (GameDetection.Normalize(picker.SelectedPath) is { } selected) path.Text = selected; else MessageBox.Show(form, UiText.Get("InvalidFolder")); } };
        Style(browse);
        layout.Controls.Add(browse, 1, 2);
        var steam = new CheckBox { Text = UiText.Get("SteamAccount"), AutoSize = true,
            Checked = string.Equals(initialPath, settings.GameFolder, StringComparison.OrdinalIgnoreCase) && settings.Account is { } account
                ? account.Steam : GameDetection.IsSteam(initialPath), Margin = new Padding(3, 12, 3, 3) };
        var trial = new CheckBox { Text = UiText.Get("FreeTrialAccount"), AutoSize = true,
            Checked = settings.Account?.FreeTrial == true };
        layout.Controls.Add(steam, 0, 3);
        layout.SetColumnSpan(steam, 2);
        layout.Controls.Add(trial, 0, 4);
        layout.SetColumnSpan(trial, 2);
        if (installations is { Length: > 0 })
        {
            var choices = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Dock = DockStyle.Fill };
            foreach (var installation in installations)
            {
                var option = new RadioButton { Text = installation, AutoSize = true,
                    MaximumSize = new Size(510, 0),
                    Checked = string.Equals(installation, initialPath, StringComparison.OrdinalIgnoreCase) };
                option.CheckedChanged += (_, _) => { if (option.Checked) path.Text = installation; };
                choices.Controls.Add(option);
            }
            layout.Controls.Add(choices, 0, 1);
            layout.SetColumnSpan(choices, 2);
            path.TextChanged += (_, _) =>
            {
                foreach (RadioButton option in choices.Controls)
                    option.Checked = string.Equals(option.Text, path.Text, StringComparison.OrdinalIgnoreCase);
            };
        }
        path.TextChanged += (_, _) => steam.Checked = GameDetection.IsSteam(path.Text);
        var save = new Button { Text = UiText.Get("Save"), AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 12, 3, 3) };
        Style(save, true);
        form.AcceptButton = save;
        save.Click += (_, _) => { if (GameDetection.Normalize(path.Text) is null) { MessageBox.Show(form, UiText.Get("InvalidFolder")); return; }
            settings = settings with { GameFolder = path.Text,
            Account = new LauncherAccount(steam.Checked, trial.Checked) }; store.Save(settings); form.DialogResult = DialogResult.OK; };
        layout.Controls.Add(save, 1, 5);
        form.Controls.Add(layout);
        form.ClientSize = new Size(570, layout.GetPreferredSize(new Size(570, 0)).Height);
        return form;
    }

    private InstallationFooter CreateInstallationFooter()
    {
        var footer = new InstallationFooter { Font = Font, BackColor = background };
        footer.SetFolder(settings.GameFolder);
        return footer;
    }

    private void BrowseGame() { SettingsDialog(); }
    private void AddHeading(string text)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Width = 700,
            Padding = new Padding(0, 0, setup.Width + 16, 0), Margin = new Padding(0, 0, 0, 18) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("GubalDesktop.icon.png")!;
        using var image = Image.FromStream(stream);
        var picture = new PictureBox { Image = new Bitmap(image), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 16, 0) };
        picture.Disposed += (_, _) => picture.Image?.Dispose();
        row.Controls.Add(picture, 0, 0);
        row.Controls.Add(new Label { Text = text, Font = new Font(Font.FontFamily, 22), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) }, 1, 0);
        content.Controls.Add(row);
    }
    private void AddText(string text, Color color, float? size = null) => content.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = color, Font = size is null ? Font : new Font(Font.FontFamily, size.Value), Margin = new Padding(0, 0, 0, 14) });
    private Button AddButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, 0, 10) };
        Style(button, primary);
        button.Click += async (_, _) => { try { await action(); } catch (Exception error) { ShowError(error); } };
        content.Controls.Add(button);
        if (primary) AcceptButton = button;
        return button;
    }
    private Button CreateUpdateButton()
    {
        var button = new Button { Text = UiText.Get("CheckUpdates"), Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(3, 6, 0, 3) };
        Style(button);
        button.Click += async (_, _) => await RunAsync(cancel => UpdateAsync(cancel, true));
        return button;
    }
    private void Style(Button button, bool primary = false)
    {
        button.Font = Font;
        button.Padding = new Padding(LogicalToDeviceUnits(14), LogicalToDeviceUnits(3), LogicalToDeviceUnits(14), LogicalToDeviceUnits(3));
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? gold : Color.FromArgb(52, 56, 61);
        button.BackColor = primary ? gold : Color.FromArgb(39, 43, 48);
        button.ForeColor = primary ? background : foreground;
        var preferred = button.GetPreferredSize(Size.Empty);
        var textHeight = TextRenderer.MeasureText("Agjpqy", button.Font).Height;
        var height = Math.Max(LogicalToDeviceUnits(primary ? 44 : 36),
            textHeight + button.Padding.Vertical + LogicalToDeviceUnits(8));
        button.AutoSize = false;
        button.MinimumSize = new Size(0, height);
        button.Size = new Size(preferred.Width, height);
        if (primary && button.Parent == content) AcceptButton = button;
    }
    private void ResizeContent()
    {
        var width = Math.Max(200, content.ClientSize.Width - content.Padding.Horizontal);
        foreach (Control control in content.Controls)
        {
              control.MaximumSize = new Size(width, int.MaxValue);
            if (ReferenceEquals(control, startButton)) control.Width = width;
            if (control is TableLayoutPanel or FlowLayoutPanel { FlowDirection: FlowDirection.RightToLeft }) control.Width = width;
            if (control is Panel { Height: 1 }) control.Width = width;
        }
    }
    private void AddSeparator() => content.Controls.Add(new Panel { Height = 1, Width = 600, BackColor = Color.FromArgb(52, 56, 61), Margin = new Padding(0, 6, 0, 20) });
    private void SetStatus(string text)
    {
        if (status.Text != text) status.Text = text;
        var visible = !string.IsNullOrWhiteSpace(text);
        if (status.Visible != visible) status.Visible = visible;
    }

    private void DisplayProgress(string text, float? fraction)
    {
        var style = fraction is null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (progress.Style != style) progress.Style = style;
        if (fraction is { } value)
        {
            var percent = Math.Clamp((int)(value * 100), 0, 100);
            if (progress.Value != percent) progress.Value = percent;
        }
        SetStatus(text);
    }
    private void ShowError(Exception error)
    {
        store.Error(error, "Desktop operation failed.");
        if (error is TranslationStartupException failure)
        {
            startupFailure = failure;
            launcherOpened = false;
            translationLoaded = false;
            SetStatus("");
            Render();
            return;
        }
        SetStatus(error.Message);
        using var details = new Form { Text = "Gubal Desktop", Size = new Size(620, 300), StartPosition = FormStartPosition.CenterParent,
            BackColor = background, ForeColor = foreground, Font = Font };
        var message = new Label { Text = error.Message, Dock = DockStyle.Fill, Padding = new Padding(24), AutoEllipsis = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(14), FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = UiText.Get("Close"), AutoSize = true, DialogResult = DialogResult.OK }; Style(close);
        var logs = new Button { Text = UiText.Get("OpenDetails"), AutoSize = true }; Style(logs);
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { store.Root }, UseShellExecute = true });
        actions.Controls.Add(close); actions.Controls.Add(logs); details.Controls.Add(message); details.Controls.Add(actions);
        details.AcceptButton = close; details.CancelButton = close; details.ShowDialog(this);
    }
    private sealed class TranslationStartupException(string message) : Exception(message);
    private sealed record CompatibilityReport(string[] DisabledParts);
}
