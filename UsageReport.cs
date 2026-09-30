using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

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
    string GubalVersion, string? Pack, string? PackVersion, string Status)
{
    public static UsageReport Create(Guid installationId, long sequence, string gubalVersion,
        UsageSnapshot snapshot) => new(1, installationId, sequence, gubalVersion,
            snapshot.Pack, snapshot.PackVersion, snapshot.Status);
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
    public async Task EnqueueAsync(UsageReport report)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{report.Sequence:D20}-{report.InstallationId:D}.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report)).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task SendAsync(HttpClient client, Uri endpoint, CancellationToken cancel,
        Action<string> diagnostic, TimeSpan? retryDelay = null)
    {
        if (!Directory.Exists(directory)) return;
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
            batch.Add(report);
            batchPaths.Add(path);
        }
        if (batch.Count == 0) return;
        if (!await UsageReporter.SendAsync(client, endpoint, batch, cancel, diagnostic, retryDelay)
                .ConfigureAwait(false)) return;
        // Keep the full batch until acceptance. The server deduplicates partial retries.
        foreach (var path in batchPaths) File.Delete(path);
    }
}
