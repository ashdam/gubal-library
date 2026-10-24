namespace GubalLibrary;

internal interface INativeHook<T> : IDisposable where T : Delegate
{
    T Original { get; }
    void Enable();
}

internal interface INativeHooks
{
    INativeHook<T> Create<T>(nint address, T detour) where T : Delegate;
}

internal interface INativeSignatures
{
    bool TryScanText(string signature, out nint address);
    bool TryGetStaticAddressFromSig(string signature, out nint address);
}
