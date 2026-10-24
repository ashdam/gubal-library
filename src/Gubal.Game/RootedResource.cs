using System.Runtime.InteropServices;
using System.Text;
using Lumina.Misc;

namespace GubalLibrary;

// The UTF-8 path must remain allocated while the game can read the resource.
internal sealed unsafe class RootedResource : IDisposable
{
    public RootedResource(string localPath)
    {
        Rooted = localPath.Replace('\\', '/');
        Hash = Crc32.Get(Rooted.ToLowerInvariant());
        var bytes = Encoding.UTF8.GetBytes(Rooted);
        Utf8 = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(Utf8, bytes.Length));
        Utf8[bytes.Length] = 0;
    }

    public string Rooted { get; }
    public uint Hash { get; }
    public byte* Utf8 { get; }
    public bool Reported { get; set; }
    public void Dispose() => NativeMemory.Free(Utf8);
}
