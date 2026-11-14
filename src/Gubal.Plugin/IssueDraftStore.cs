using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace GubalLibrary;

internal sealed record IssueReportDraft(IssueReport Report, bool Submitted, IssueReportReceipt? Receipt, bool CanSubmit = true,
    bool IncludeAlias = true, string? AliasText = null, int AutomaticStatusChecks = 0, byte[]? OriginalImage = null,
    string? ImageFile = null, string? OriginalFile = null);

internal sealed class IssueDraftStore(string path)
{
    private readonly object sync = new();
    private readonly ConcurrentQueue<Exception> errors = new();
    private Task pending = Task.CompletedTask;

    public Task Pending { get { lock (this.sync) return this.pending; } }
    public bool TryGetError(out Exception? error) => this.errors.TryDequeue(out error);

    public IssueReportDraft? Read(string previousPath)
    {
        if (!File.Exists(path) && File.Exists(previousPath)) File.Move(previousPath, path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 6 * 1024 * 1024) throw new InvalidDataException("The draft is too large.");
        var draft = JsonSerializer.Deserialize<IssueReportDraft>(File.ReadAllText(path), IssueReport.Json)
            ?? throw new InvalidDataException("The draft is empty.");
        if (draft.Report is null) throw new InvalidDataException("The draft is empty.");
        var image = this.ReadImage(draft.ImageFile, draft.Report.Id) ?? draft.Report.Image;
        var original = this.ReadImage(draft.OriginalFile, draft.Report.Id) ?? draft.OriginalImage;
        draft = draft with { Report = draft.Report with { Image = image }, OriginalImage = original };
        if (draft.Report.SchemaVersion != 1 || draft.Report.Id == Guid.Empty
            || draft.Report.InstallationId == Guid.Empty || draft.Report.Comment is not { Length: <= IssueReport.MaxCommentLength }
            || draft.Report.Alias is not { Length: <= IssueReport.MaxAliasLength }
            || draft.AliasText?.Length > IssueReport.MaxAliasLength * 4
            || draft.Report.ReceiptToken is not { Length: 64 }
            || image?.Length > IssueReport.MaxImageBytes || original?.Length > IssueReport.MaxImageBytes)
            throw new InvalidDataException("The draft is invalid.");
        return draft;
    }

    public Task SaveAsync(IssueReportDraft draft) => this.Enqueue(async () =>
    {
        var image = await this.WriteImageAsync(draft.Report.Image, draft.Report.Id).ConfigureAwait(false);
        var original = await this.WriteImageAsync(draft.OriginalImage, draft.Report.Id).ConfigureAwait(false);
        var metadata = draft with { Report = draft.Report with { Image = null }, OriginalImage = null,
            ImageFile = image, OriginalFile = original };
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(metadata, IssueReport.Json)).ConfigureAwait(false);
        File.Move(temporary, path, true);
        this.RemoveImages(image, original);
        return true;
    });

    public Task DeleteAsync() => this.Enqueue(() =>
    {
        this.Delete();
        return Task.FromResult(true);
    });

    public Task ClearHistoryAsync(IssueReportHistory history) => this.Enqueue(() =>
    {
        history.Clear();
        return Task.FromResult(true);
    });

    public Task<IReadOnlyList<SentIssueReport>> ArchiveAsync(IssueReportHistory history, IssueReport report, string issueUrl) =>
        this.Enqueue(() =>
        {
            var entries = history.Add(report, issueUrl);
            // Save the history before the draft is removed.
            this.Delete();
            return Task.FromResult(entries);
        });

    private Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        lock (this.sync)
        {
            var previous = this.pending;
            var next = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                try { return await operation().ConfigureAwait(false); }
                catch (Exception error) { this.errors.Enqueue(error); throw; }
            });
            this.pending = next;
            return next;
        }
    }

    private byte[]? ReadImage(string? name, Guid id)
    {
        if (name is null) return null;
        var prefix = Path.GetFileName(path) + "." + id.ToString("N") + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.Ordinal)
            || name.Length != prefix.Length + 64 + 4
            || name.AsSpan(prefix.Length, 64).ContainsAnyExcept("0123456789abcdef"))
            throw new InvalidDataException("The draft image name is invalid.");
        var file = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (new FileInfo(file).Length > IssueReport.MaxImageBytes) throw new InvalidDataException("The draft image is too large.");
        return File.ReadAllBytes(file);
    }

    private async Task<string?> WriteImageAsync(byte[]? bytes, Guid id)
    {
        if (bytes is null) return null;
        if (bytes.Length > IssueReport.MaxImageBytes) throw new InvalidDataException("The draft image is too large.");
        var file = path + "." + id.ToString("N") + "." + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".png";
        if (!File.Exists(file))
        {
            await File.WriteAllBytesAsync(file + ".tmp", bytes).ConfigureAwait(false);
            File.Move(file + ".tmp", file, true);
        }
        return Path.GetFileName(file);
    }

    private void Delete()
    {
        File.Delete(path);
        this.RemoveImages(null, null);
    }

    private void RemoveImages(string? image, string? original)
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.png"))
            if (Path.GetFileName(file) != image && Path.GetFileName(file) != original) File.Delete(file);
    }
}
