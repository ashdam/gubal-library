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
        Hash = Crc32.Get(LowercaseForHash(Rooted));
        var bytes = Encoding.UTF8.GetBytes(Rooted);
        Utf8 = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(Utf8, bytes.Length));
        Utf8[bytes.Length] = 0;
    }

    public string Rooted { get; }
    public uint Hash { get; }
    public byte* Utf8 { get; }
    public bool Reported { get; set; }

    // Resource hashes use ASCII case conversion. Keep other path characters unchanged.
    internal static string LowercaseForHash(string path) => string.Create(path.Length, path, (result, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            result[index] = value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
        }
    });

    public void Dispose() => NativeMemory.Free(Utf8);
}
