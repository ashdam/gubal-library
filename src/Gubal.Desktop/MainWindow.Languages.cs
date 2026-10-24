using GubalLibrary;

namespace GubalDesktop;

internal sealed partial class MainWindow
{
    private readonly ToolTip tooltips = new() { InitialDelay = 350, AutoPopDelay = 20000, ReshowDelay = 100 };
    private readonly CancellationTokenSource catalogCancellation = new();
    private readonly Dictionary<string, PackManifest?> catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RadioButton> languageButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PictureBox> reviewerIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> flags = new(StringComparer.OrdinalIgnoreCase);
    private Image? reviewerImage;
    private Button? languageAction;

    private async Task LoadCatalogAsync()
    {
        var cancel = catalogCancellation.Token;
        var installer = new PackInstaller(store, store.Root);
        try
        {
            await Task.WhenAll(KnownPacks.All.Select(async pack =>
            {
                var published = pack.ManifestUrl is { } url ? await installer.FetchManifestAsync(url, cancel) : null;
                cancel.ThrowIfCancellationRequested();
                if (IsDisposed) return;
                catalog[pack.Code] = published ?? PackManifest.Read(Path.Combine(store.Root, "packs", pack.Code, "pack")).Manifest;
                if (languageButtons.TryGetValue(pack.Code, out var button) && !button.IsDisposed)
                    UpdateLanguageButton(button, pack);
            }));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private void AddLanguageChoices()
    {
        languageButtons.Clear();
        reviewerIcons.Clear();
        var lineHeight = TextRenderer.MeasureText("Ag", Font).Height;
        var rowHeight = Math.Max(LogicalToDeviceUnits(68), lineHeight * 2 + LogicalToDeviceUnits(24));
        var englishHeight = Math.Max(LogicalToDeviceUnits(56), lineHeight + LogicalToDeviceUnits(24));
        var headerHeight = lineHeight + LogicalToDeviceUnits(12);
        var height = headerHeight + rowHeight * KnownPacks.All.Length + englishHeight;
        var choices = new TableLayoutPanel { ColumnCount = 2, RowCount = KnownPacks.All.Length + 2, AutoSize = false,
            Height = height, MinimumSize = new Size(0, height),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize, Width = 700, Margin = new Padding(0, 2, 0, 12) };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(56)));
        choices.RowStyles.Add(new RowStyle(SizeType.Absolute, headerHeight));
        choices.Controls.Add(new Label { Text = UiText.Get("Language"), Dock = DockStyle.Fill,
            ForeColor = muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        for (var index = 0; index < KnownPacks.All.Length; index++)
        {
            var pack = KnownPacks.All[index];
            var radio = LanguageButton(pack.Code);
            radio.Image = Flag(pack.Code);
            var review = new PictureBox { Size = new Size(LogicalToDeviceUnits(32), LogicalToDeviceUnits(32)),
                Anchor = AnchorStyles.None, SizeMode = PictureBoxSizeMode.Zoom,
                AccessibleName = UiText.Get("Reviewers"), TabStop = false };
            tooltips.SetToolTip(review, UiText.Get("Reviewers"));
            reviewerIcons.Add(pack.Code, review);
            UpdateLanguageButton(radio, pack);
            languageButtons.Add(pack.Code, radio);
            choices.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            choices.Controls.Add(radio, 0, index + 1);
            choices.Controls.Add(review, 1, index + 1);
        }
        var english = LanguageButton("en");
        english.Text = "🌐  " + UiText.Get("EnglishOption");
        tooltips.SetToolTip(english, UiText.Get("EnglishTooltip"));
        choices.RowStyles.Add(new RowStyle(SizeType.Absolute, englishHeight));
        choices.Controls.Add(english, 0, KnownPacks.All.Length + 1);
        choices.SetColumnSpan(english, 2);
        content.Controls.Add(choices);
    }

    private RadioButton LanguageButton(string code)
    {
        var radio = new RadioButton
        {
            Appearance = Appearance.Button, Checked = (languageChoice ?? settings.Language) == code,
            AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ImageAlign = ContentAlignment.MiddleLeft, TextImageRelation = TextImageRelation.ImageBeforeText,
            Padding = new Padding(12, 4, 8, 4), Margin = new Padding(0, 0, 8, 8),
            FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = foreground,
            AccessibleName = code == "en" ? "English, no localization" : KnownPacks.ForCode(code)?.Name,
            AutoEllipsis = false,
        };
        radio.FlatAppearance.BorderColor = radio.Checked ? gold : Color.FromArgb(52, 56, 61);
        radio.FlatAppearance.CheckedBackColor = Color.FromArgb(43, 39, 31);
        radio.CheckedChanged += (_, _) =>
        {
            radio.FlatAppearance.BorderColor = radio.Checked ? gold : Color.FromArgb(52, 56, 61);
            if (!radio.Checked) return;
            languageChoice = code;
            if (languageAction is { IsDisposed: false })
            {
                languageAction.Text = code == "en" ? UiText.Get("UseEnglish") : UiText.Get("DownloadPrepare");
                languageAction.Enabled = true;
            }
        };
        return radio;
    }

    private void AddLanguageActions(bool showBack = false)
    {
        languageAction = AddButton((languageChoice ?? settings.Language) == "en" ? UiText.Get("UseEnglish") : UiText.Get("DownloadPrepare"), () => RunAsync(InstallAsync), true);
        languageAction.Enabled = !string.IsNullOrEmpty(languageChoice ?? settings.Language);
        languageFooter.Size = new Size(ClientSize.Width, languageAction.Height + languageFooter.Padding.Vertical);
        languageFooter.Visible = true;
        if (showBack)
        {
            var back = AddButton(UiText.Get("Back"), () =>
            {
                if (Manifest is null && settings.Language != "en" &&
                    (languageChoice is null || languageChoice == settings.Language)) languageChoice = "en";
                chooseLanguage = false;
                Render();
                return Task.CompletedTask;
            });
            back.Location = new Point(languageFooter.Padding.Left,
                languageFooter.Padding.Top + (languageAction.Height - back.Height) / 2);
            back.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            languageFooter.Controls.Add(back);
        }
        languageAction.Location = new Point(languageFooter.ClientSize.Width - languageFooter.Padding.Right - languageAction.Width,
            languageFooter.Padding.Top);
        languageAction.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        languageFooter.Controls.Add(languageAction);
    }

    private void UpdateLanguageButton(RadioButton button, KnownPack pack)
    {
        var loaded = catalog.TryGetValue(pack.Code, out var manifest);
        var coverage = manifest?.Coverage;
        var progressText = coverage is not null ? UiText.Format("CoveragePercent", coverage.Percent)
            : loaded ? UiText.Get("ProgressUnavailable") : UiText.Get("LoadingProgress");
        button.Text = $" {pack.Name}\n {progressText}";
        button.AccessibleDescription = progressText;
        if (reviewerIcons.TryGetValue(pack.Code, out var review))
        {
            var reviewers = manifest?.ReviewerNames.ToArray() ?? [];
            var reviewed = reviewers.Length > 0;
            review.Visible = reviewed;
            if (reviewed)
            {
                if (reviewerImage is null)
                {
                    using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("GubalDesktop.reviewer.png")!;
                    using var source = Image.FromStream(stream);
                    reviewerImage = new Bitmap(source);
                }
                review.Image = reviewerImage;
                var reviewText = UiText.Get("Reviewers") + "\n" + string.Join("\n", reviewers.Select(name => "@" + name));
                tooltips.SetToolTip(review, reviewText);
                button.AccessibleDescription += ". " + reviewText;
            }
        }
        tooltips.SetToolTip(button, coverage is not null ? CoverageTooltip(pack.Name, coverage)
            : pack.Name + "\n" + progressText);
    }

    private Image? Flag(string code)
    {
        if (flags.TryGetValue(code, out var image)) return image;
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"GubalDesktop.flags.{code.ToLowerInvariant()}.png");
        if (stream is null) return null;
        using var source = Image.FromStream(stream);
        image = new Bitmap(source, new Size(28, 18));
        flags.Add(code, image);
        return image;
    }

    private static string CoverageTooltip(string name, PackCoverage coverage)
    {
        var rows = coverage.ByExpansion.Where(expansion => expansion.Total > 0)
            .Select(expansion => $"{expansion.Name}: {expansion.Percent:0.#} %");
        return name + " · " + UiText.Format("CoveragePercent", coverage.Percent) + "\n\n" + string.Join("\n", rows)
            + UiText.Get("CoverageScope");
    }

    private void AddSelectedLanguage(PackManifest? manifest, string? selectedCode = null)
    {
        var code = selectedCode ?? settings.Language;
        var row = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = false, Width = 700,
            Margin = new Padding(0, 0, 0, 12) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var label = new Label { Text = manifest?.LanguageName ?? KnownPacks.ForCode(code)?.Name ?? UiText.Get("EnglishOption"), Font = new Font(Font.FontFamily, 17),
            AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 4, 0, 4) };
        Control flag = code == "en"
            ? new Label { Text = "🌐", AutoSize = true, Anchor = AnchorStyles.Left }
            : new PictureBox { Image = Flag(code), Size = new Size(28, 18), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left };
        var change = new Button { Text = UiText.Get("ChangeLanguage"), AutoSize = true, Anchor = AnchorStyles.Right,
              Padding = new Padding(12, 6, 12, 6), Margin = new Padding(3, 3, 0, 3) };
        Style(change);
        row.Height = Math.Max(change.Height + change.Margin.Vertical, label.GetPreferredSize(Size.Empty).Height);
        row.MinimumSize = new Size(0, row.Height);
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, row.Height));
        change.Click += (_, _) => { chooseLanguage = true; Render(); };
        row.Controls.Add(flag, 0, 0); row.Controls.Add(label, 1, 0); row.Controls.Add(change, 2, 0);
        content.Controls.Add(row);
        if (manifest is null) return;
        var details = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, Margin = new Padding(0, 6, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        if (updateStatus?.State == UpdateState.UpToDate)
        {
            var versionLine = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            versionLine.Controls.Add(new Label { Text = UiText.Get("PackUpToDate"), AutoSize = true, Margin = Padding.Empty,
                ForeColor = SystemInformation.HighContrast ? foreground : Color.FromArgb(155, 213, 175) });
            versionLine.Controls.Add(new Label { Text = " · " + manifest.TranslationVersion, ForeColor = muted,
                AutoSize = true, Margin = Padding.Empty });
            details.Controls.Add(versionLine);
        }
        else
            details.Controls.Add(new Label { Text = UiText.Format("InstalledPack", manifest.TranslationVersion),
                ForeColor = muted, AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
        if (manifest.Coverage is { } coverage)
        {
            var progressLabel = new Label { Text = UiText.Format("CoveragePercent", coverage.Percent), AutoSize = true,
                ForeColor = muted, Margin = new Padding(0, 0, 0, 10) };
            tooltips.SetToolTip(progressLabel, CoverageTooltip(manifest.LanguageName ?? Selected.Name, coverage));
            details.Controls.Add(progressLabel);
        }
        var update = CreateUpdateButton();
        change.Width = update.Width = Math.Max(change.Width, update.Width);
        row.RowCount = 2;
        row.Controls.Add(details, 0, 1);
        row.SetColumnSpan(details, 2);
        row.Controls.Add(update, 2, 1);
        var detailsHeight = Math.Max(details.GetPreferredSize(Size.Empty).Height + details.Margin.Vertical,
            update.Height + update.Margin.Vertical);
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, detailsHeight));
        row.Height += detailsHeight;
        row.MinimumSize = new Size(0, row.Height);
    }

    private void FitInitialWindow()
    {
        PerformLayout();
        content.PerformLayout();
        var preferred = content.GetPreferredSize(new Size(content.ClientSize.Width, 0)).Height;
        var required = Height - content.ClientSize.Height + preferred;
        var area = Screen.FromControl(this).WorkingArea;
        Height = Math.Min(Math.Max(Height, required), area.Height - 32);
        Top = Math.Max(area.Top, area.Top + (area.Height - Height) / 2);
    }
}
