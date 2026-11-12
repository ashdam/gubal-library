namespace GubalLibrary.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("gubal-tests-").FullName;

    public string Write(string relative)
    {
        var file = System.IO.Path.Combine(this.Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, []);
        return file;
    }

    public void Dispose() => Directory.Delete(this.Path, recursive: true);
}
