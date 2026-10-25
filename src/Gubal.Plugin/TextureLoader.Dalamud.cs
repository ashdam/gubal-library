using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

internal static unsafe class DalamudTextureLoader
{
    public static (TextureLoader? Loader, string? Error) Create(IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log, IEnumerable<string> paths) =>
        TextureLoader.Create(new DalamudHooks(interop), new DalamudSignatures(scanner), new DalamudLog(log), paths);

    private sealed class DalamudHooks(IGameInteropProvider interop) : INativeHooks
    {
        public INativeHook<T> Create<T>(nint address, T detour) where T : Delegate =>
            new DalamudHook<T>(interop.HookFromAddress(address, detour));
    }

    private sealed class DalamudHook<T>(Hook<T> hook) : INativeHook<T> where T : Delegate
    {
        public T Original => hook.OriginalDisposeSafe;
        public void Enable() => hook.Enable();
        public void Dispose() => hook.Dispose();
    }

    private sealed class DalamudSignatures(ISigScanner scanner) : INativeSignatures
    {
        public bool TryScanText(string signature, out nint address) => scanner.TryScanText(signature, out address);
        public bool TryGetStaticAddressFromSig(string signature, out nint address) =>
            scanner.TryGetStaticAddressFromSig(signature, out address);
    }

    private sealed class DalamudLog(IPluginLog log) : IPackInstallLog
    {
        public void Information(string message, params object[] values) => Diagnostics.Log(log, message, values);
        public void Warning(string message, params object[] values) => log.Warning(message, values);
        public void Warning(Exception error, string message, params object[] values) => log.Warning(error, message, values);
        public void Error(Exception error, string message, params object[] values) => log.Error(error, message, values);
    }
}
