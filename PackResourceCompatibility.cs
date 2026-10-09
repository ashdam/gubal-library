using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheapLoc;
using Lumina;
using Lumina.Data;

namespace GubalLibrary;

internal sealed class CompatibilitySelection
{
    public Dictionary<string, string> Excluded { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RequiredFailed { get; set; }
    public bool Cached { get; set; }
    public HashSet<string> Covered { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Requested { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Allows(string path) => !this.RequiredFailed && !this.Excluded.ContainsKey(path);
}

internal static class PackResourceCompatibility
{
    public const string FileName = "gubal-compatibility.json";
    private const string CachePolicy = "1";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static (CompatibilitySelection? Selection, string? Error) Check(string folder, PackManifest manifest,
        string? running, string sourceRoot, string cacheFolder, Func<bool> canWait, Action<string> warning)
    {
        var versionError = PackVersion.Error(manifest.GameVersion, running);
        if (versionError is null) return (null, null);
        if (string.IsNullOrWhiteSpace(manifest.GameVersion) || string.IsNullOrWhiteSpace(running))
            return (null, versionError);
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return (null, versionError);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var document = JsonSerializer.Deserialize<Document>(bytes, Json)
                ?? throw new InvalidDataException("Empty compatibility metadata.");
            Validate(document);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                CachePolicy + "\n" + running + "\n" + manifest.GameVersion + "\n" + Path.GetFullPath(folder) + "\n" + sourceRoot + "\n" +
                Convert.ToHexString(SHA256.HashData(bytes)))));
            var cachePath = Path.Combine(cacheFolder, key + ".json");
            var cached = ReadCache(cachePath, key, document);
            if (cached is not null)
            {
                cached.Cached = true;
                return (cached, null);
            }
            if (!canWait())
                return (null, Loc.Localize("Compatibility.WaitRequired",
                    "Enable Dalamud's wait for plugins before the game loads, then restart to check this older pack."));

            // Shared Lumina can already serve translations after a plugin reload.
            using var original = new GameData(sourceRoot, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
            var matchesBySource = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var cacheable = true;
            foreach (var (source, hash) in document.Sources)
            {
                try
                {
                    var file = original.GetFile<FileResource>(source);
                    var matches = hash is null ? file is null : file is not null &&
                        Convert.ToHexString(SHA256.HashData(file.Data)).Equals(hash, StringComparison.OrdinalIgnoreCase);
                    matchesBySource.Add(source, matches);
                }
                catch (Exception)
                {
                    matchesBySource.Add(source, false);
                    cacheable = false;
                }
            }
            var result = Select(document, matchesBySource);
            try
            {
                if (cacheable)
                {
                    Directory.CreateDirectory(cacheFolder);
                    var temporary = cachePath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new Cache(key, matchesBySource), Json));
                    File.Move(temporary, cachePath, true);
                }
            }
            catch (Exception error) { warning($"Could not save the pack compatibility result: {error.Message}"); }
            return (result, null);
        }
        catch (Exception error)
        {
            warning($"Could not verify the older language pack: {error.Message}");
            return (null, Loc.Localize("Compatibility.Invalid",
                "The older pack's compatibility metadata could not be verified. Update the language pack."));
        }
    }

    public static CompatibilitySelection Restrict(CompatibilitySelection selection, IEnumerable<string> paths)
    {
        selection.Requested = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in selection.Requested.Where(path => !selection.Covered.Contains(path)))
        {
            selection.Excluded.TryAdd(path, FileName);
            selection.RequiredFailed |= path.StartsWith("common/font/", StringComparison.OrdinalIgnoreCase);
        }
        return selection;
    }

    private static CompatibilitySelection? ReadCache(string path, string key, Document document)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllBytes(path), Json);
            if (cache?.Key != key || cache.Matches is null || cache.Matches.Count != document.Sources.Count ||
                cache.Matches.Keys.Any(path => !document.Sources.ContainsKey(path))) return null;
            return Select(document, cache.Matches);
        }
        catch (Exception) { return null; }
    }

    private static CompatibilitySelection Select(Document document, IReadOnlyDictionary<string, bool> matches)
    {
        var excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requiredFailed = false;
        foreach (var group in document.Groups.Values)
        {
            var failure = group.Sources.FirstOrDefault(path => !matches[path]);
            if (failure is null) continue;
            requiredFailed |= group.Required;
            foreach (var resource in group.Files) excluded.Add(resource, failure);
        }
        return new CompatibilitySelection
        {
            Excluded = excluded,
            RequiredFailed = requiredFailed,
            Covered = document.Groups.Values.SelectMany(group => group.Files).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static void Validate(Document document)
    {
        if (document.Version != 1 || document.Groups.Count == 0 || document.Sources.Count == 0)
            throw new InvalidDataException("Unsupported compatibility metadata.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.Groups.Values.Count(group => group.Required) > 1)
            throw new InvalidDataException("Fonts must use one required compatibility group.");
        foreach (var (path, hash) in document.Sources)
        {
            if (!ValidPath(path) || hash is not null && (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("Invalid compatibility source.");
        }
        foreach (var group in document.Groups.Values)
        {
            if (group.Files.Length == 0 || group.Sources.Length == 0 ||
                group.Sources.Any(path => !document.Sources.ContainsKey(path)))
                throw new InvalidDataException("Incomplete compatibility group.");
            foreach (var file in group.Files)
            {
                if (!ValidPath(file) || !files.Add(file) || !group.Sources.Contains(file))
                    throw new InvalidDataException("Invalid compatibility resource.");
            }
            if (group.Files.Any(path => path.StartsWith("common/font/", StringComparison.Ordinal)) != group.Required)
                throw new InvalidDataException("Font compatibility must cover the complete required bundle.");
            if (group.Sources.Any(path => document.Sources[path] is null &&
                (!group.Required || !path.StartsWith("common/font/", StringComparison.Ordinal))))
                throw new InvalidDataException("Only generated font resources may have no original.");
            if (group.Required && (!group.Sources.Any(path => path.EndsWith(".fdt", StringComparison.Ordinal) && document.Sources[path] is not null) ||
                !group.Sources.Any(path => path.EndsWith(".tex", StringComparison.Ordinal) && document.Sources[path] is not null)))
                throw new InvalidDataException("Font compatibility must identify original tables and textures.");
            foreach (var page in group.Files.Where(path => path.EndsWith(".exd", StringComparison.Ordinal)))
            {
                var header = System.Text.RegularExpressions.Regex.Replace(page, @"_\d+(?:_[a-z]+)?\.exd$", ".exh");
                if (header == page || !group.Sources.Contains(page) || !group.Sources.Contains(header) ||
                    document.Sources[page] is null || document.Sources[header] is null)
                    throw new InvalidDataException("Page compatibility must include its original and header.");
            }
        }
    }

    private static bool ValidPath(string path) => path.Length > 0 && !path.Contains('\\') &&
        !path.Contains(':') && !path.StartsWith('/') && path.Split('/').All(part => part is not ("" or "." or "..")) &&
        (path.StartsWith("exd/", StringComparison.Ordinal) || path.StartsWith("ui/", StringComparison.Ordinal) ||
         path.StartsWith("common/font/", StringComparison.Ordinal));

    private sealed class Document
    {
        public int Version { get; init; }
        public Dictionary<string, string?> Sources { get; init; } = [];
        public Dictionary<string, Group> Groups { get; init; } = [];
    }

    private sealed record Group(string[] Files, string[] Sources, bool Required);
    private sealed record Cache(string Key, Dictionary<string, bool> Matches);
}
