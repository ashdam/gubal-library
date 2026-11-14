using System.Net.Http;
using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace GubalLibrary;

internal sealed class IssueReporter : IDisposable
{
    private const int MaxAutomaticStatusChecks = 12;
    private readonly PluginUi ui;
    private readonly Configuration config;
    private readonly Func<IssueReport?> createReport;
    private readonly Func<string?> tracker;
    private readonly Func<string> playerName;
    private readonly ReportCaptureEditor editor;
    private readonly IssueReportClient client;
    private readonly IPluginLog log;
    private readonly IssueDraftStore storage;
    private Task saveTask = Task.CompletedTask;
    private Task? deleteTask;
    private Task? clearHistoryTask;
    private Task<IReadOnlyList<SentIssueReport>>? archiveTask;
    private readonly IssueReportHistory historyStorage;
    private IReadOnlyList<SentIssueReport> history = [];
    private SentIssueReport? lastSent;
    private bool archiveRequested;
    private bool? historyOpenRequest;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private readonly CancellationTokenSource cancel = new();
    private IssueReportDraft? draft;
    private Task<IssueReportReceipt>? sendTask;
    private bool includeAlias = true;
    private bool dirty;
    private string comment = string.Empty;
    private string alias = string.Empty;
    private string? errorKey;
    private DateTime saveAfter;
    private bool disposed;
    private bool captureRequested;
    private bool unreadableDraft;
    private DateTime nextStatusCheck;
    private bool shortcutDown;
    private int shortcutRequested;

    public bool Selecting => this.editor.Selecting;
    private bool StorageBusy => this.deleteTask is not null || this.archiveTask is not null || this.clearHistoryTask is not null;

    public IssueReporter(Configuration config, PluginUi ui, Func<IssueReport?> createReport,
        Func<string?> tracker, Func<string> playerName, ITextureProvider textures, ITextureReadbackProvider readback, IPluginLog log, string folder)
    {
        this.config = config;
        this.ui = ui;
        this.createReport = createReport;
        this.tracker = tracker;
        this.playerName = playerName;
        this.editor = new ReportCaptureEditor(config, ui, textures, readback,
            () => this.captureRequested = true, () => this.OpenTab?.Invoke(), this.ImageChanged, this.CaptureError);
        this.client = new IssueReportClient(this.http, new Uri(UsageReporter.Endpoint, "/api/gubal/issue-reports"));
        this.log = log;
        this.storage = new IssueDraftStore(Path.Combine(folder, UsageReporter.Endpoint.IsLoopback ? "issue-draft-local.json" : "issue-draft.json"));
        this.historyStorage = new IssueReportHistory(Path.Combine(folder,
            UsageReporter.Endpoint.IsLoopback ? "issue-history-local.json" : "issue-history.json"));
        try { this.history = this.historyStorage.Read(); }
        catch (Exception error) { this.Fail("History", error); }
        try
        {
            var previousPath = Path.Combine(folder, UsageReporter.Endpoint.IsLoopback ? "translation-draft-local.json" : "translation-draft.json");
            this.draft = this.storage.Read(previousPath);
            if (this.draft is { } saved)
            {
                this.comment = saved.Report.Comment;
                this.alias = saved.AliasText ?? saved.Report.Alias;
                this.includeAlias = saved.IncludeAlias && (!saved.Submitted || saved.Report.Alias.Length > 0);
                if (!saved.CanSubmit && !saved.Submitted && this.createReport() is { } active)
                    this.draft = saved with { CanSubmit = true, Report = active with
                    {
                        Id = saved.Report.Id, ReceiptToken = saved.Report.ReceiptToken,
                        Comment = saved.Report.Comment, Alias = saved.Report.Alias, Image = saved.Report.Image,
                    } };
                this.archiveRequested = saved.Receipt is { Status: "created", IssueUrl: not null };
                if (!this.archiveRequested && saved.Report.Image is { Length: > 0 } image)
                    this.editor.Restore(image, saved.OriginalImage);
            }
        }
        catch (Exception error) { this.draft = null; this.unreadableDraft = true; this.Fail("Draft", error); }
    }

    public Action? OpenTab { get; set; }
    public Func<bool>? IsVisible { get; set; }

    private bool Button(string label, bool primary = false, bool compact = false, FontAwesomeIcon? icon = null) =>
        this.ui.DrawActionButton(label, primary, compact, icon: icon);

    private void AlignButtons(string label, bool compact = false) =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0,
            ImGui.GetContentRegionAvail().X - this.ui.MeasureActionButton(label, compact).X));

    public void Open()
    {
        this.OpenTab?.Invoke();
        this.captureRequested = !this.unreadableDraft && this.draft?.Submitted != true
            && this.draft?.Report.Image is null && string.IsNullOrWhiteSpace(this.comment);
    }

    public void UpdateShortcut(IKeyState keys, bool loggedIn)
    {
        var key = (VirtualKey)((int)VirtualKey.F1 + Math.Clamp(this.config.ReportHotkeyFunction, 1, 12) - 1);
        var down = loggedIn && keys[key]
            && keys[VirtualKey.CONTROL] == this.config.ReportHotkeyCtrl
            && keys[VirtualKey.SHIFT] == this.config.ReportHotkeyShift
            && keys[VirtualKey.MENU] == this.config.ReportHotkeyAlt;
        if (down && !this.shortcutDown) Interlocked.Exchange(ref this.shortcutRequested, 1);
        this.shortcutDown = down;
    }

    public void Draw()
    {
        if (this.disposed) return;
        this.CompleteTasks();
        if (this.archiveRequested)
        {
            this.archiveRequested = false;
            this.ArchiveConfirmedReport();
        }
        if (!this.StorageBusy && this.IsVisible?.Invoke() == true && this.sendTask is null && this.draft?.Receipt?.Status is "received" or "processing"
            && this.draft.AutomaticStatusChecks < MaxAutomaticStatusChecks && DateTime.UtcNow >= this.nextStatusCheck)
        {
            this.draft = this.draft with { AutomaticStatusChecks = this.draft.AutomaticStatusChecks + 1 };
            if (!this.SaveDraft())
            {
                this.nextStatusCheck = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            this.nextStatusCheck = DateTime.UtcNow.AddSeconds(10);
            this.sendTask = this.RequestAsync(this.draft.Report, false, this.saveTask);
        }
        if (this.StorageBusy) return;
        if (this.captureRequested) { this.captureRequested = false; this.Capture(); }
        if (this.dirty && DateTime.UtcNow >= this.saveAfter) this.SaveDraft();
        if (Interlocked.Exchange(ref this.shortcutRequested, 0) != 0
            && !this.editor.Selecting && this.sendTask is null && !this.editor.Busy)
        {
            this.Capture();
        }
        if (this.editor.Selecting) { this.editor.DrawSelection(); return; }
        if (this.IsVisible?.Invoke() != true) this.editor.ReleaseScreen();
    }

    private void PrepareDraft()
    {
        if (this.draft is null)
        {
            var report = this.createReport();
            // Capture does not require pack metadata. Sending does.
            this.draft = new(report ?? new IssueReport(1, Guid.NewGuid(),
                Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                Guid.NewGuid(), string.Empty, null, string.Empty, null, string.Empty, string.Empty, null), false, null, report is not null);
            this.comment = string.Empty;
            this.alias = this.playerName();
            this.includeAlias = true;
        }
    }

    private void Capture()
    {
        if (this.unreadableDraft) { this.OpenTab?.Invoke(); return; }
        if (this.StorageBusy || this.editor.Busy || this.sendTask is not null) return;
        if (this.draft?.Submitted is true)
        {
            if (this.draft.Receipt?.Status != "created")
            {
                this.errorKey = "ReportPending";
                this.OpenTab?.Invoke();
                return;
            }
            this.ArchiveConfirmedReport();
            if (this.draft is not null) return;
        }
        this.PrepareDraft();
        this.lastSent = null;
        this.editor.BeginCapture();
    }

    public void DrawForm()
    {
        if (!this.unreadableDraft) this.PrepareDraft();
        using var storageDisabled = ImRaii.Disabled(this.StorageBusy);
        if (this.draft is null)
        {
            if (this.unreadableDraft)
            {
                ImGui.TextWrapped(this.ErrorText());
                if (this.Button(T("Discard", "Discard / new report"))) ImGui.OpenPopup("###UnreadableReport");
                if (ImGui.BeginPopup("###UnreadableReport"))
                {
                    ImGui.TextWrapped(T("DiscardConfirm", "Remove the local draft? A report already received by the server remains there."));
                    this.AlignButtons(T("ConfirmDiscard", "Remove local draft"));
                    if (this.Button(T("ConfirmDiscard", "Remove local draft")))
                    {
                        try { this.deleteTask ??= this.storage.DeleteAsync(); }
                        catch (Exception error) { this.Fail("Draft", error); }
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }
                this.DrawHistory();
                return;
            }
            if (this.editor.Busy)
                ImGui.TextUnformatted(T("Working", "Working..."));
            return;
        }
        var locked = this.StorageBusy || this.draft.Submitted || this.sendTask is not null || this.editor.Busy;
        if (!this.draft.CanSubmit)
        {
            ImGui.TextWrapped(T("CaptureOnly", "You can capture and save a draft. Sending requires an active language pack with a supported issue tracker."));
            if (this.tracker() is { } url && this.Button(T("Tracker", "Open the language pack issue tracker"))) Util.OpenLink(url);
        }
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var split = ImGui.GetContentRegionAvail().X >= 700f * scale;
        if (split)
        {
            using var columns = ImRaii.Table("##issueWorkspace", 2, ImGuiTableFlags.SizingStretchProp);
            if (columns)
            {
                ImGui.TableSetupColumn("##capture", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("##details", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(1);
                var descriptionBottom = this.DrawReportFields(locked);
                ImGui.TableSetColumnIndex(0);
                this.editor.DrawScreenshot(locked, 278f * scale, descriptionBottom);
            }
        }
        else
        {
            this.editor.DrawScreenshot(locked, 204f * scale);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            this.DrawReportFields(locked);
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        this.DrawFooter();
        this.DrawHistory();
    }

    private float DrawReportFields(bool locked)
    {
        using var disabled = ImRaii.Disabled(locked);
        var reclaimedHeight = this.DrawMetadata(locked);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        PluginUi.DrawRequiredLabel(T("Comment", "Describe the issue"));
        ImGui.TextWrapped(T("DescriptionHint", "Include the current text and your suggested correction."));
        if (PluginUi.TextArea("###ReportComment", ref this.comment, IssueReport.MaxCommentLength * 4,
                new Vector2(-1, 100f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale + reclaimedHeight))) this.Changed();
        var descriptionBottom = ImGui.GetItemRectMax().Y;
        ImGui.TextDisabled(string.Format(T("CharacterCount", "{0}/{1}"), this.comment.Length, IssueReport.MaxCommentLength));
        if (this.comment.Length > IssueReport.MaxCommentLength)
            DrawReportError(string.Format(T("CommentLimit", "Use {0} characters or fewer. Shorten the description before you save or send this report."),
                IssueReport.MaxCommentLength));
        ImGui.Spacing();
        return descriptionBottom;
    }

    private void DrawFooter()
    {
        var report = this.draft!;
        var busy = this.StorageBusy || this.sendTask is not null || this.editor.Busy;
        var archiveNeeded = report.Receipt is { Status: "created", IssueUrl: not null };
        var action = archiveNeeded ? T("SaveHistory", "Save report history")
            : this.lastSent is not null ? T("OpenIssue", "Open issue")
            : report.Submitted ? T("Retry", "Send again") : T("Send", "Send report");
        var discard = T("Discard", "Discard / new report");
        FontAwesomeIcon? actionIcon = !archiveNeeded && this.lastSent is null ? FontAwesomeIcon.PaperPlane : null;
        var buttonsWidth = this.ui.MeasureActionButton(discard, false).X
            + this.ui.MeasureActionButton(action, false, icon: actionIcon).X + ImGui.GetStyle().ItemSpacing.X;
        using (var table = ImRaii.Table("##reportActions", 2, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##status", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##buttons", ImGuiTableColumnFlags.WidthFixed, buttonsWidth);
                ImGui.TableNextColumn();
                this.DrawStatus(busy);
                ImGui.TableNextColumn();
                using (ImRaii.Disabled(busy))
                {
                    if (this.Button(discard)) ImGui.OpenPopup("###DiscardIssueReport");
                    ImGui.SameLine();
                    var valid = archiveNeeded || this.lastSent is not null || this.CanSend;
                    using (ImRaii.Disabled(!valid))
                    {
                        if (this.Button(action, true, icon: actionIcon))
                        {
                            if (archiveNeeded) this.ArchiveConfirmedReport();
                            else if (this.lastSent is { } sent) Util.OpenLink(sent.IssueUrl);
                            else this.Send();
                        }
                    }
                }
                if (ImGui.BeginPopup("###DiscardIssueReport"))
                {
                    ImGui.TextWrapped(T("DiscardConfirm", "Remove the local draft? A report already received by the server remains there."));
                    this.AlignButtons(T("ConfirmDiscard", "Remove local draft"));
                    if (this.Button(T("ConfirmDiscard", "Remove local draft")))
                    {
                        try { this.deleteTask ??= this.storage.DeleteAsync(); }
                        catch (Exception error) { this.Fail("Draft", error); }
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }
            }
        }
    }

    private void DrawStatus(bool busy)
    {
        if (this.errorKey is not null) DrawReportError(this.ErrorText());
        if (this.draft is not { } report) return;
        if (busy) ImGui.TextWrapped(T("Working", "Working..."));
        else if (this.lastSent is not null || report.Receipt?.Status == "created")
            ImGui.TextWrapped(T("Created", "The issue has been created."));
        else if (report.Submitted)
        {
            if (report.Receipt?.Status == "failed")
                DrawReportError(T("Failed", "The report is saved on the server, but the issue could not be created. Keep the report ID for support."));
            else if (report.Receipt?.Status is "received" or "processing" && report.AutomaticStatusChecks >= MaxAutomaticStatusChecks)
                DrawReportError(T("PollingStopped", "Issue creation could not be confirmed after 12 checks. Select Send again to retry the same report."));
            else
                ImGui.TextWrapped(report.Receipt is not null ? T("Received", "Report received. Issue creation is pending.")
                    : T("Unconfirmed", "Delivery is not confirmed. Retry the same report to check or send it safely."));
            ImGui.TextWrapped(report.Report.Id.ToString());
        }
        if (!busy && !report.Submitted && this.lastSent is null && this.errorKey is null
            && (report.Report.Image is not { Length: > 0 } || string.IsNullOrWhiteSpace(this.comment)))
            ImGui.TextWrapped(T("RequiredHint", "Add a screenshot and describe the issue to send a report."));
        if (report.Submitted && report.Receipt?.Status != "created")
            ImGui.TextWrapped(string.Format(T("StatusChecks", "Status checks: {0}/{1}"), report.AutomaticStatusChecks, MaxAutomaticStatusChecks));
    }

    private void DrawHistory()
    {
        ImGui.Spacing();
        ImGui.Separator();
        var clear = T("ClearHistory", "Clear report history");
        var requested = this.historyOpenRequest;
        this.historyOpenRequest = null;
        var expanded = false;
        var openClear = false;
        using (var header = ImRaii.Table("##reportHistoryHeader", 2, ImGuiTableFlags.SizingStretchProp))
        {
            if (header)
            {
                ImGui.TableSetupColumn("##title", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##clear", ImGuiTableColumnFlags.WidthFixed, this.ui.MeasureActionButton(clear, true).X);
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0,
                    (PluginTheme.CompactActionHeight * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale - ImGui.GetFrameHeight()) / 2));
                expanded = PluginUi.Disclosure(string.Format(T("History", "Report history locally saved ({0})"), this.history.Count)
                    + "###ReportHistory", requested);
                ImGui.TableNextColumn();
                using (ImRaii.Disabled(this.StorageBusy || this.sendTask is not null || this.editor.Busy
                    || this.history.Count == 0 && this.errorKey != "History"))
                    openClear = this.Button(clear, compact: true);
            }
        }
        if (openClear) ImGui.OpenPopup("###ClearReportHistory");
        var open = true;
        if (ImGui.BeginPopupModal(clear + "###ClearReportHistory", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(T("ClearHistoryConfirm", "Remove all saved reports from this device? Issues on GitHub will stay unchanged."));
            if (this.Button(T("Cancel", "Cancel"))) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (this.Button(clear, true))
            {
                this.clearHistoryTask ??= this.storage.ClearHistoryAsync(this.historyStorage);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!expanded) return;
        if (this.history.Count == 0)
        {
            ImGui.TextDisabled(T("HistoryEmpty", "No confirmed reports are stored."));
            return;
        }
        using var table = ImRaii.Table("##reportHistory", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg);
        if (!table) return;
        ImGui.TableSetupColumn(T("HistoryDate", "Confirmed"), ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(T("Pack", "Language pack"), ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(T("Comment", "Describe the issue"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##issueLink", ImGuiTableColumnFlags.WidthFixed,
            this.ui.MeasureActionButton(T("OpenIssue", "Open issue"), false).X);
        ImGui.TableHeadersRow();
        foreach (var entry in this.history)
        {
            using var id = ImRaii.PushId(entry.Id.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.ConfirmedAt.ToLocalTime().ToString("g"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Pack);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{T("PackVersion", "Pack version")}: {entry.PackVersion}\n{T("GubalVersion", "Gubal version")}: {entry.GubalVersion}\n{T("GameVersion", "Game version")}: {entry.GameVersion}");
            ImGui.TableNextColumn();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(entry.Comment);
            ImGui.PopTextWrapPos();
            if (entry.PlayerName.Length > 0)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextUnformatted(entry.PlayerName);
            }
            ImGui.TableNextColumn();
            if (this.Button(T("OpenIssue", "Open issue"))) Util.OpenLink(entry.IssueUrl);
        }
    }

    private void ResetDraft()
    {
        this.draft = null; this.dirty = false; this.lastSent = null;
        this.editor.Reset();
        this.comment = this.alias = string.Empty; this.errorKey = null;
        this.captureRequested = false;
    }

    private void ArchiveConfirmedReport()
    {
        if (this.draft is not { Receipt.Status: "created", Receipt.IssueUrl: { } issue } saved) return;
        this.archiveTask ??= this.storage.ArchiveAsync(this.historyStorage, saved.Report, issue);
    }

    private static void DrawReportError(string message)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.94f, 0.3f, 0.3f, 1));
        ImGui.TextWrapped(message);
    }

    private float DrawMetadata(bool locked)
    {
        var report = this.draft!.Report;
        PluginUi.DrawSubheading(T("Metadata", "Report details"));
        var gameLabel = T("GameVersion", "Game version");
        var gubalLabel = T("GubalVersion", "Gubal version");
        var packLabel = T("Pack", "Language pack");
        var versionLabel = T("PackVersion", "Pack version");
        var leftLabelWidth = Math.Max(ImGui.CalcTextSize(gameLabel).X, ImGui.CalcTextSize(packLabel).X);
        var rightLabelWidth = Math.Max(ImGui.CalcTextSize(gubalLabel).X, ImGui.CalcTextSize(versionLabel).X);
        var leftValueWidth = Math.Max(ImGui.CalcTextSize(report.GameVersion ?? "?").X, ImGui.CalcTextSize(report.Pack).X);
        var rightValueWidth = Math.Max(ImGui.CalcTextSize(report.GubalVersion).X, ImGui.CalcTextSize(report.PackVersion ?? "?").X);
        var twoPairs = leftLabelWidth + rightLabelWidth + leftValueWidth + rightValueWidth
            + ImGui.GetStyle().CellPadding.X * 8 <= ImGui.GetContentRegionAvail().X;
        using (var table = ImRaii.Table("##reportMetadata", twoPairs ? 4 : 2, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##leftLabel", ImGuiTableColumnFlags.WidthFixed,
                    twoPairs ? leftLabelWidth : Math.Max(leftLabelWidth, rightLabelWidth));
                ImGui.TableSetupColumn("##leftValue", twoPairs ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch,
                    twoPairs ? leftValueWidth : 1f);
                if (twoPairs)
                {
                    ImGui.TableSetupColumn("##rightLabel", ImGuiTableColumnFlags.WidthFixed, rightLabelWidth);
                    ImGui.TableSetupColumn("##rightValue", ImGuiTableColumnFlags.WidthStretch);
                }
                MetadataRow(gameLabel, report.GameVersion);
                MetadataRow(gubalLabel, report.GubalVersion);
                MetadataRow(packLabel, report.Pack);
                MetadataRow(versionLabel, report.PackVersion);
            }
        }
        if (!locked && this.includeAlias && string.IsNullOrEmpty(this.alias) && this.playerName() is { Length: > 0 } name)
        {
            this.alias = name;
            this.Changed();
        }
        ImGui.Spacing();
        if (PluginUi.Checkbox(T("Alias", "Player name"), ref this.includeAlias)) this.Changed();
        ImGui.SameLine();
        using (ImRaii.Disabled(!this.includeAlias))
        {
            ImGui.SetNextItemWidth(-1);
            if (PluginUi.TextField("###ReportAlias", ref this.alias, IssueReport.MaxAliasLength * 4)) this.Changed();
        }
        ImGui.TextDisabled(T("NameHint", "Uncheck to send without your name."));
        if (this.includeAlias && this.alias.Length > IssueReport.MaxAliasLength)
            DrawReportError(string.Format(T("NameLimit", "Use {0} characters or fewer for the player name."), IssueReport.MaxAliasLength));
        return ImGui.GetFrameHeightWithSpacing() + (twoPairs ? ImGui.GetTextLineHeightWithSpacing() * 2 : 0);
    }

    private static void MetadataRow(string label, string? value)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrEmpty(value) ? "?" : value);
    }

    private void Changed() { this.lastSent = null; this.dirty = true; this.saveAfter = DateTime.UtcNow.AddSeconds(1); }

    private bool FieldsWithinLimits => this.comment.Length <= IssueReport.MaxCommentLength
        && (!this.includeAlias || this.alias.Length <= IssueReport.MaxAliasLength);

    private bool CanSend => this.draft is { CanSubmit: true } report
        && (report.Submitted || (report.Report.Image is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(this.comment) && this.FieldsWithinLimits));

    private bool SaveDraft()
    {
        if (this.draft is null) return true;
        if (!this.draft.Submitted && !this.FieldsWithinLimits)
        {
            this.saveAfter = DateTime.UtcNow.AddSeconds(30);
            return false;
        }
        try
        {
            if (!this.draft.Submitted)
                this.draft = this.draft with { Report = this.draft.Report with { Comment = this.comment, Alias = this.includeAlias ? this.alias : string.Empty },
                    IncludeAlias = this.includeAlias, AliasText = this.alias };
            this.saveTask = this.storage.SaveAsync(this.draft);
            this.dirty = false; return true;
        }
        catch (Exception error) { this.Fail("Draft", error); this.saveAfter = DateTime.UtcNow.AddSeconds(30); return false; }
    }

    private async Task<IssueReportReceipt> RequestAsync(IssueReport report, bool submit, Task saved)
    {
        // Persist the exact request before it can reach the server.
        await saved.ConfigureAwait(false);
        return submit
            ? await this.client.SubmitAsync(report, this.cancel.Token).ConfigureAwait(false)
            : await this.client.StatusAsync(report, this.cancel.Token).ConfigureAwait(false);
    }

    private void Send()
    {
        if (!this.CanSend || !this.SaveDraft()) return;
        this.draft = this.draft! with { Submitted = true, AutomaticStatusChecks = 0 };
        if (!this.SaveDraft()) return;
        this.editor.ReleaseScreen();
        this.errorKey = null;
        this.sendTask = this.RequestAsync(this.draft.Report, true, this.saveTask);
    }

    private void CompleteTasks()
    {
        this.editor.CompleteTasks();
        while (this.storage.TryGetError(out var storageError))
        {
            this.Fail("Draft", storageError!);
            this.dirty = this.draft is not null;
            this.saveAfter = DateTime.UtcNow.AddSeconds(30);
        }
        if (this.clearHistoryTask?.IsCompleted is true)
        {
            try
            {
                this.clearHistoryTask.GetAwaiter().GetResult();
                this.history = [];
                this.historyOpenRequest = false;
                this.lastSent = null;
                if (this.errorKey == "History") this.errorKey = null;
            }
            catch (Exception error) { this.Fail("History", error); }
            this.clearHistoryTask = null;
        }
        if (this.deleteTask?.IsCompleted is true)
        {
            try
            {
                this.deleteTask.GetAwaiter().GetResult();
                var capture = this.unreadableDraft;
                this.ResetDraft();
                this.unreadableDraft = false;
                this.captureRequested = capture;
            }
            catch (Exception error) { this.Fail("Draft", error); }
            this.deleteTask = null;
        }
        if (this.archiveTask?.IsCompleted is true)
        {
            try
            {
                this.history = this.archiveTask.GetAwaiter().GetResult();
                this.historyOpenRequest = true;
                var entry = this.history.First(item => item.Id == this.draft!.Report.Id);
                this.ResetDraft();
                this.lastSent = entry;
            }
            catch (Exception error) { this.Fail("History", error); }
            this.archiveTask = null;
        }

        if (this.sendTask?.IsCompleted is true)
        {
            try
            {
                var receipt = this.sendTask.GetAwaiter().GetResult();
                if (this.draft is not null) this.draft = this.draft with { Receipt = receipt };
                this.archiveRequested = receipt is { Status: "created", IssueUrl: not null };
                this.nextStatusCheck = DateTime.UtcNow.AddSeconds(10);
                this.SaveDraft();
            }
            catch (Exception error)
            {
                if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest
                    or System.Net.HttpStatusCode.RequestEntityTooLarge or System.Net.HttpStatusCode.UnsupportedMediaType
                    or System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.TooManyRequests }
                    && this.draft?.Receipt is null && this.draft is not null)
                {
                    this.draft = this.draft with { Submitted = false };
                    this.SaveDraft();
                }
                this.nextStatusCheck = DateTime.UtcNow.AddSeconds(30);
                this.Fail("SendError", error);
            }
            this.sendTask = null;
        }
    }

    private void ImageChanged(byte[]? image, byte[]? original)
    {
        if (this.draft is null) return;
        this.draft = this.draft with { Report = this.draft.Report with { Image = image }, OriginalImage = original };
        this.Changed();
        this.SaveDraft();
    }

    private void CaptureError(string? key, Exception? error)
    {
        this.errorKey = key;
        if (error is not null) this.Fail(key!, error);
        if (key == "Draft") { this.draft = null; this.unreadableDraft = true; }
    }

    private void Fail(string key, Exception error)
    {
        this.errorKey = key;
        this.log.Warning(error, "Issue report operation failed.");
    }

    private string ErrorText() => this.errorKey switch
    {
        "ImageDimensions" => T("ImageDimensions", "Select a smaller area. Width and height must not exceed 2048 pixels."),
        "ImageBytes" => T("ImageBytes", "The PNG exceeds 2 MiB. Select a smaller area."),
        "History" => T("HistoryError", "The local report history could not be read or saved. The current report is kept."),
        "Draft" => T("DraftError", "The local draft could not be saved or read. No report was deleted."),
        "SendError" => T("SendError", "The server could not be reached or rejected the request. The draft is kept. Try again later."),
        "ReportPending" => T("ReportPending", "Check the current report delivery before you capture another issue."),
        _ => T("CaptureError", "The screenshot could not be prepared. Try capturing or selecting the area again."),
    };

    private static string T(string key, string fallback) => Loc.Localize("Report." + key, fallback);

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        if (!this.StorageBusy) this.SaveDraft();
        this.cancel.Cancel();
        this.editor.Dispose();
        // Finish local writes before another plugin instance reads the draft.
        try { this.storage.Pending.GetAwaiter().GetResult(); }
        catch (Exception error) { this.Fail("Draft", error); }
        var pending = Task.WhenAll(new Task?[] { this.sendTask }
            .OfType<Task>().Select(async task => { try { await task.ConfigureAwait(false); } catch { } }));
        _ = pending.ContinueWith(_ => { this.http.Dispose(); this.cancel.Dispose(); }, TaskScheduler.Default);
    }
}
