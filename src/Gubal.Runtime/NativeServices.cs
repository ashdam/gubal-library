using System.Reflection.PortableExecutable;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using GubalLibrary;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;

namespace GubalStandalone;

internal sealed class NativeCompatibilityException(string message) : Exception(message);

internal static class NativeCompatibility
{
    public static void Check(string executable, bool includeAssets)
    {
        var scanner = new ImageSignatures(executable, 0);
        Require(FileThread.Addresses.DoFileJob.String, "File reader");
        if (!includeAssets) return;
        Require(AssetResources.ReadFileSignature, "Local file reader");
        Require(ResourceManager.Addresses.GetResourceSync.String, "Synchronous resource loader");
        Require(ResourceManager.Addresses.GetResourceAsync.String, "Asynchronous resource loader");
        if (TextureLoader.ValidateSignatures(scanner) is { } error)
            throw new NativeCompatibilityException("Texture loading is not compatible: " + error);

        void Require(string signature, string name)
        {
            if (!scanner.TryScanText(signature, out _))
                throw new NativeCompatibilityException(name + ": the game function could not be identified uniquely.");
        }
    }
}

internal sealed class StandaloneHooks : INativeHooks
{
    public INativeHook<T> Create<T>(nint address, T detour) where T : Delegate =>
        new NativeHook<T>(ReloadedHooks.Instance.CreateHook(detour, address));

    private sealed class NativeHook<T>(IHook<T> hook) : INativeHook<T> where T : Delegate
    {
        public T Original => hook.OriginalFunction;
        public void Enable() => hook.Activate();
        public void Dispose() => hook.Disable();
    }
}

internal sealed class ImageSignatures : INativeSignatures
{
    private readonly byte[] text;
    private readonly nint address;
    private readonly (long Start, long End)[] readableRanges;

    public ImageSignatures(string executable, nint imageBase)
    {
        using var stream = File.OpenRead(executable);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus)
            throw new NativeCompatibilityException("The game executable is not a supported Windows x64 image.");
        var sections = pe.PEHeaders.SectionHeaders.Where(item => item.Name == ".text").ToArray();
        if (sections.Length != 1 || !sections[0].SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute))
            throw new NativeCompatibilityException("The game executable has no unique executable code section.");
        var section = sections[0];
        text = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
        address = imageBase + section.VirtualAddress;
        readableRanges = pe.PEHeaders.SectionHeaders.Where(item => item.SectionCharacteristics.HasFlag(SectionCharacteristics.MemRead))
            .Select(item => ((long)imageBase + item.VirtualAddress, (long)imageBase + item.VirtualAddress + item.VirtualSize)).ToArray();
    }

    private bool Find(string signature, out int offset)
    {
        var pattern = signature.Split(' ').Select(part => part == "??" ? (byte?)null : Convert.ToByte(part, 16)).ToArray();
        var anchor = Array.FindIndex(pattern, value => value.HasValue);
        offset = -1;
        if (anchor < 0 || pattern.Length > text.Length) return false;
        for (var index = 0; index <= text.Length - pattern.Length; index++)
        {
            var next = text.AsSpan(index + anchor, text.Length - pattern.Length - index + 1).IndexOf(pattern[anchor]!.Value);
            if (next < 0) break;
            index += next;
            var matches = true;
            for (var part = 0; part < pattern.Length; part++)
                if (pattern[part] is { } value && text[index + part] != value) { matches = false; break; }
            if (!matches) continue;
            if (offset >= 0) return false;
            offset = index;
        }
        return offset >= 0;
    }

    public bool TryScanText(string signature, out nint result)
    {
        result = 0;
        if (!Find(signature, out var offset)) return false;
        result = address + offset;
        if (text[offset] is 0xE8 or 0xE9)
        {
            if (offset + 5 > text.Length) return false;
            result += 5 + BitConverter.ToInt32(text, offset + 1);
        }
        return result >= address && result < address + text.Length;
    }

    public bool TryGetStaticAddressFromSig(string signature, out nint result)
    {
        result = 0;
        if (!Find(signature, out var offset)) return false;
        // The texture LOD signature starts with a seven-byte RIP-relative MOV.
        if (offset + 7 > text.Length || text[offset] != 0x48 || text[offset + 1] != 0x8B || text[offset + 2] != 0x05) return false;
        result = address + offset + 7 + BitConverter.ToInt32(text, offset + 3);
        var target = (long)result;
        return readableRanges.Any(range => target >= range.Start && target + nint.Size <= range.End);
    }
}

internal sealed class RuntimeLog(Action<string> write) : IPackInstallLog
{
    private int traces;
    public void Information(string message, params object[] values)
    {
        if (Interlocked.Increment(ref traces) <= 12) Write(message, values);
    }
    public void Warning(string message, params object[] values) => Write(message, values);
    public void Warning(Exception error, string message, params object[] values) => Write(message + " " + error.Message, values);
    public void Error(Exception error, string message, params object[] values) => Write(message + " " + error, values);
    private void Write(string message, object[] values) => write(message + " " + string.Join(" ", values));
}
