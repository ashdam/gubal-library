using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using GubalLibrary;
using InteropGenerator.Runtime;

namespace GubalStandalone;

internal sealed unsafe class AssetResources : IDisposable
{
    internal const string ReadFileSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? "
        + "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42";

    private readonly Dictionary<string, RootedResource> resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> owned = [];
    private readonly INativeHook<ResourceManager.Delegates.GetResourceSync> sync;
    private readonly INativeHook<ResourceManager.Delegates.GetResourceAsync> async;
    public Dictionary<string, string> RootedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AssetResources(IReadOnlyDictionary<string, string> selected, ImageSignatures signatures, RuntimeLog log)
    {
        try
        {
            foreach (var (path, local) in selected.Where(item => IsAsset(item.Key)))
            {
                var resource = new RootedResource(local);
                owned.Add(resource);
                resources.Add(path, resource);
                RootedPaths.Add(resource.Rooted, local);
            }
            var hooks = new StandaloneHooks();
            if (ResourceManager.Addresses.GetResourceSync.Value == 0 || ResourceManager.Addresses.GetResourceAsync.Value == 0)
                throw new InvalidOperationException("Resource loader signatures were not found.");
            var (textures, error) = TextureLoader.Create(hooks, signatures, log,
                resources.Where(item => item.Key.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).Select(item => item.Value.Rooted));
            if (error is not null) throw new InvalidOperationException("Texture loader signature failed: " + error);
            owned.Add(textures!);
            sync = hooks.Create<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetSync);
            owned.Add(sync);
            async = hooks.Create<ResourceManager.Delegates.GetResourceAsync>(ResourceManager.Addresses.GetResourceAsync.Value, GetAsync);
            owned.Add(async);
            sync.Enable();
            async.Enable();
        }
        catch { Dispose(); throw; }
    }

    internal static bool IsAsset(string path) => path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fdt", StringComparison.OrdinalIgnoreCase);

    private RootedResource? Find(CStringPointer path, void* parameters)
    {
        if (!path.HasValue || parameters != null && *(uint*)((byte*)parameters + 20) != 0) return null;
        return resources.GetValueOrDefault(Encoding.UTF8.GetString(path.AsSpan()));
    }

    private ResourceHandle* GetSync(ResourceManager* manager, ResourceCategory* category, uint* type,
        uint* hash, CStringPointer path, void* parameters, void* debugPtr, uint debugInt)
    {
        var resource = Find(path, parameters);
        if (resource is null) return sync.Original(manager, category, type, hash, path, parameters, debugPtr, debugInt);
        var rootedHash = resource.Hash;
        return sync.Original(manager, category, type, &rootedHash, resource.Utf8, parameters, debugPtr, debugInt);
    }

    private ResourceHandle* GetAsync(ResourceManager* manager, ResourceCategory* category, uint* type,
        uint* hash, CStringPointer path, void* parameters, bool unknown, void* debugPtr, uint debugInt)
    {
        var resource = Find(path, parameters);
        if (resource is null) return async.Original(manager, category, type, hash, path, parameters, unknown, debugPtr, debugInt);
        var rootedHash = resource.Hash;
        return async.Original(manager, category, type, &rootedHash, resource.Utf8, parameters, unknown, debugPtr, debugInt);
    }

    public void Dispose()
    {
        for (var index = owned.Count - 1; index >= 0; index--) owned[index].Dispose();
        owned.Clear();
    }
}
