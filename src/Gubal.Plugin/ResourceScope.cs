using Dalamud.Plugin.Services;

namespace GubalLibrary;

// Release dependent resources before their dependencies, including after partial initialization.
internal sealed class ResourceScope(IPluginLog log) : IDisposable
{
    private readonly List<IDisposable> resources = [];
    private bool disposed;

    public T Own<T>(T resource) where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        for (var i = this.resources.Count - 1; i >= 0; i--)
        {
            try { this.resources[i].Dispose(); }
            catch (Exception error) { log.Error(error, "Could not release a plugin resource."); }
        }
        this.resources.Clear();
    }
}
