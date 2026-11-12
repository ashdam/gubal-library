using Dalamud.Plugin.Services;

namespace GubalLibrary;

internal static class DalamudTextureLoader
{
    public static (TextureLoader? Loader, string? Error) Create(IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log, IEnumerable<string> paths) =>
        TextureLoader.Create(new DalamudGameHooks(interop), new DalamudGameSignatures(scanner),
            new DalamudGameLog(log), paths);
}
