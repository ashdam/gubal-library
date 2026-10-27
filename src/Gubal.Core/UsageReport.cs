using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GubalLibrary;

internal sealed record UsageSnapshot(string? Pack, string? PackVersion, string Status)
{
    public static UsageSnapshot Create(string? officialPack, string? version, bool configured,
        bool readable, bool enabled, bool active, bool compatible)
    {
        if (!configured) return new(null, null, "no_pack");
        var pack = officialPack ?? "custom";
        var packVersion = officialPack is null ? null : version;
        var status = !readable ? "error" : !enabled ? "disabled" : active ? "active"
            : !compatible ? "incompatible" : "error";
        return new(pack, packVersion, status);
    }

    public static string? OfficialPack(string source, bool managed,
        IEnumerable<(string Code, string? Source)> knownPacks)
    {
        if (!managed) return null;
        foreach (var pack in knownPacks)
            if (string.Equals(source, pack.Source, StringComparison.Ordinal))
                return pack.Code.ToLowerInvariant();
        return null;
    }
}

internal sealed record UsageReport(int SchemaVersion, Guid InstallationId, long Sequence,
    string GubalVersion, string? Pack, string? PackVersion, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Client = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UsageError[]? Errors = null)
{
    public static UsageReport Create(Guid installationId, long sequence, string gubalVersion,
        UsageSnapshot snapshot, string? client = null) => new(client is null ? 1 : 2, installationId, sequence, gubalVersion,
            snapshot.Pack, snapshot.PackVersion, snapshot.Status, client);
}

internal static class UsageReporter
{
#if GUBAL_USAGE_LOCAL
    internal static readonly Uri Endpoint = new("http://127.0.0.1:8787/api/gubal/report");
#else
    internal static readonly Uri Endpoint = new("https://eorzealocalized.com/api/gubal/report");
#endif

    public static async Task<bool> SendAsync(HttpClient client, Uri endpoint, IReadOnlyList<UsageReport> reports,
        CancellationToken cancel, Action<string> diagnostic, TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? TimeSpan.FromSeconds(5);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var wait = delay * (attempt + 1);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var response = await client.PostAsJsonAsync(endpoint, reports, timeout.Token)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        diagnostic("Startup usage batch accepted.");
                        return true;
                    }
                    if (response.StatusCode is not HttpStatusCode.RequestTimeout
                        and not HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                    {
                        diagnostic($"Startup usage report rejected ({(int)response.StatusCode}).");
                        return false;
                    }
                    var retry = response.Headers.RetryAfter;
                    var requested = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                    if (requested > TimeSpan.FromMinutes(1))
                    {
                        diagnostic("Startup usage report deferred until the next game start.");
                        return false;
                    }
                    if (requested > wait) wait = requested.Value;
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                {
                    if (cancel.IsCancellationRequested) return false;
                }
                if (attempt < 2) await Task.Delay(wait, cancel).ConfigureAwait(false);
            }
            diagnostic("Startup usage report could not be sent.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        return false;
    }
}

internal sealed class UsageOutbox(string directory)
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim writing = new(1, 1);
    public UsageErrorQueue Errors { get; } = new(Path.Combine(directory, "errors"));

    public async Task EnqueueAsync(UsageReport report, bool deferred = false)
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try { await SaveAsync(report, deferred).ConfigureAwait(false); }
        finally { writing.Release(); }
    }

    private async Task SaveAsync(UsageReport report, bool deferred)
    {
        if (deferred)
        {
            await WriteAsync(Path.Combine(directory, "deferred"), report).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(directory);
        var assigned = new HashSet<Guid>();
        foreach (var pending in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<UsageReport>(await File.ReadAllTextAsync(pending).ConfigureAwait(false));
                foreach (var error in saved?.Errors ?? []) assigned.Add(error.Id);
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        var selected = new List<UsageError>();
        report = report with { SchemaVersion = 3, Client = report.Client ?? "dalamud", Errors = [] };
        foreach (var error in Errors.Pending().Where(error => !assigned.Contains(error.Id)))
        {
            var candidate = report with { Errors = [.. selected, error] };
            if (JsonSerializer.SerializeToUtf8Bytes(new[] { candidate }, WireJson).Length > 8192) break;
            selected.Add(error);
            report = candidate;
            if (selected.Count == 4) break;
        }
        await WriteAsync(directory, report).ConfigureAwait(false);
    }

    private static async Task WriteAsync(string folder, UsageReport report)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{report.Sequence:D20}-{report.InstallationId:D}.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report)).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task SendAsync(HttpClient client, Uri endpoint, CancellationToken cancel,
        Action<string> diagnostic, TimeSpan? retryDelay = null)
    {
        if (!Directory.Exists(directory)) return;
        var deferred = Path.Combine(directory, "deferred");
        if (Directory.Exists(deferred))
            foreach (var path in Directory.GetFiles(deferred, "*.json").OrderByDescending(Path.GetFileName))
            {
                cancel.ThrowIfCancellationRequested();
                UsageReport? report;
                try { report = JsonSerializer.Deserialize<UsageReport>(await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false)); }
                catch (JsonException) { diagnostic("A deferred usage report could not be read. The file was kept."); continue; }
                if (report is null) continue;
                if (!File.Exists(Path.Combine(directory, Path.GetFileName(path))))
                    await EnqueueAsync(report).ConfigureAwait(false);
                File.Delete(path);
            }
        // Send the current state first. Keep older reports for history within the server quota.
        var paths = Directory.GetFiles(directory, "*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(10);
        var batch = new List<UsageReport>();
        var batchPaths = new List<string>();
        foreach (var path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            UsageReport? report;
            try
            {
                report = JsonSerializer.Deserialize<UsageReport>(
                    await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false));
            }
            catch (JsonException)
            {
                diagnostic("A pending usage report could not be read. The file was kept.");
                continue;
            }
            if (report is null) continue;
            if (batch.Count > 0 && batch[0].InstallationId != report.InstallationId) continue;
            if (JsonSerializer.SerializeToUtf8Bytes(batch.Append(report).ToArray(), WireJson).Length > 8192) break;
            batch.Add(report);
            batchPaths.Add(path);
        }
        if (batch.Count == 0) return;
        if (!await UsageReporter.SendAsync(client, endpoint, batch, cancel, diagnostic, retryDelay)
                .ConfigureAwait(false)) return;
        // Keep the full batch until acceptance. The server deduplicates partial retries.
        foreach (var error in batch.SelectMany(report => report.Errors ?? [])) Errors.Acknowledge(error);
        foreach (var path in batchPaths) File.Delete(path);
    }
}
