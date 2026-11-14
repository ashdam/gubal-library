using System.Text.Json;

namespace GubalLibrary;

internal sealed record SentIssueReport(Guid Id, DateTimeOffset ConfirmedAt, string IssueUrl,
    string Pack, string? PackVersion, string GubalVersion, string? GameVersion, string PlayerName, string Comment);

internal sealed class IssueReportHistory(string path)
{
    public void Clear() => File.Delete(path);

    public IReadOnlyList<SentIssueReport> Read()
    {
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        var entries = JsonSerializer.Deserialize<List<SentIssueReport>>(stream, IssueReport.Json)
            ?? throw new InvalidDataException("The report history is empty.");
        foreach (var entry in entries)
            if (entry is null || entry.Id == Guid.Empty || entry.Comment is not { Length: <= IssueReport.MaxCommentLength }
                || entry.PlayerName is not { Length: <= IssueReport.MaxAliasLength }
                || !Uri.TryCreate(entry.IssueUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com")
                throw new InvalidDataException("The report history is invalid.");
        return entries;
    }

    public IReadOnlyList<SentIssueReport> Add(IssueReport report, string issueUrl)
    {
        var entries = this.Read().ToList();
        if (entries.Any(entry => entry.Id == report.Id)) return entries;
        entries.Insert(0, new SentIssueReport(report.Id, DateTimeOffset.UtcNow, issueUrl, report.Pack,
            report.PackVersion, report.GubalVersion, report.GameVersion, report.Alias, report.Comment));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, IssueReport.Json));
        File.Move(temporary, path, true);
        return entries;
    }
}
