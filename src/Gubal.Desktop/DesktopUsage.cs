using System.Net.Http;
using System.Text.Json;
using GubalLibrary;

namespace GubalDesktop;

internal sealed class DesktopUsage(DesktopStore store) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly UsageOutbox outbox = new(Path.Combine(store.Root, "usage-pending"));
    private Task sending = Task.CompletedTask;

    public async Task RecordAsync(UsageSnapshot snapshot)
    {
        try
        {
            var path = Path.Combine(store.Root, "usage.json");
            var identity = File.Exists(path)
                ? JsonSerializer.Deserialize<Identity>(File.ReadAllText(path)) ?? new()
                : new Identity();
            identity = new(identity.InstallationId == Guid.Empty ? Guid.NewGuid() : identity.InstallationId,
                checked(identity.Sequence + 1));
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(identity));
            File.Move(path + ".tmp", path, overwrite: true);
            var report = UsageReport.Create(identity.InstallationId, identity.Sequence,
                typeof(DesktopUsage).Assembly.GetName().Version!.ToString(), snapshot, "desktop");
            await outbox.EnqueueAsync(report);
            Flush();
        }
        catch (Exception error)
        {
            store.Warning(error, "Could not save the Desktop usage report.");
        }
    }

    public void Flush()
    {
        if (!sending.IsCompleted) return;
        var cancel = cancellation.Token;
        sending = Task.Run(async () =>
        {
            try
            {
                await outbox.SendAsync(client, UsageReporter.Endpoint, cancel,
                    message => store.Information(message));
            }
            catch (Exception error)
            {
                if (!cancel.IsCancellationRequested)
                    store.Warning(error, "Could not send the Desktop usage reports.");
            }
        });
    }

    public void Dispose()
    {
        cancellation.Cancel();
        client.Dispose();
        cancellation.Dispose();
    }

    private sealed record Identity(Guid InstallationId = default, long Sequence = 0);
}
