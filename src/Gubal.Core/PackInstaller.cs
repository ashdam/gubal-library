using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace GubalLibrary;

internal sealed class PackInstaller
{
    private const string InstalledFolder = "pack";

    private const string StagingFolder = "pack.staging";
    private const string BackupFolder = "pack.backup";
    private const string PendingFolder = "pack.pending";
    private const string PendingBackupFolder = "pack.pending.backup";
    private const string PendingFile = "gubal-pending-install.json";

    private const string DownloadFile = "pack.download.zip";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);

    private readonly IPackInstallLog log;
    private readonly string configDirectory;

    public PackInstaller(IPackInstallLog log, string configDirectory)
    {
        this.log = log;
        this.configDirectory = configDirectory;
    }

    public string InstalledPath => Path.Combine(this.configDirectory, InstalledFolder);

    public static bool IsRemote(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsArchive(string source) =>
        source.Trim().EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public bool HasPending => File.Exists(Path.Combine(this.configDirectory, PendingFolder, PendingFile))
        || File.Exists(Path.Combine(this.configDirectory, PendingBackupFolder, PendingFile))
        || File.Exists(Path.Combine(this.InstalledPath, PendingFile));

    public string? ReadPendingSource()
    {
        try
        {
            foreach (var folder in new[] { PendingFolder, PendingBackupFolder, InstalledFolder })
            {
                var path = Path.Combine(this.configDirectory, folder, PendingFile);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<PendingPack>(File.ReadAllText(path))?.Source;
            }
        }
        catch (Exception error) { this.log.Warning(error, "Could not read the pending pack selection."); }
        return null;
    }

    public async Task<InstallResult> InstallAsync(string source, IProgress<InstallProgress>? progress = null,
        CancellationToken cancel = default, bool prepareForRestart = false)
    {
        source = source.Trim();
        if (source.Length == 0) return InstallResult.Failed("Nothing to install.");
        try
        {
            if (!IsRemote(source)) this.RequireExternalSource(source);
            using var installLock = this.LockInstallation();
            var destination = Path.Combine(this.configDirectory, prepareForRestart ? PendingFolder : InstalledFolder);
            var backup = Path.Combine(this.configDirectory, prepareForRestart ? PendingBackupFolder : BackupFolder);
            RecoverDirectory(destination, backup);
            var staging = Path.Combine(this.configDirectory, StagingFolder);
            var download = Path.Combine(this.configDirectory, DownloadFile);
            DeleteIfPresent(staging);
            try
            {
                InstallResult result;
                string root;
                var custom = !IsRemote(source) && !IsArchive(source);
                if (custom)
                {
                    result = this.Adopt(Path.GetFullPath(source));
                    if (!result.Success) return result;
                    if (!prepareForRestart) return result;
                    root = staging;
                    Directory.CreateDirectory(root);
                }
                else
                {
                    if (IsRemote(source)) await this.DownloadAsync(source, progress, cancel).ConfigureAwait(false);
                    else download = source;
                    if (!File.Exists(download)) return InstallResult.Failed($"No archive at '{source}'.");
                    Extract(download, staging, progress, cancel);
                    progress?.Report(InstallProgress.Working("Checking the pack"));
                    result = ValidateArchive(staging);
                    if (!result.Success) return result;
                    root = result.Path;
                }
                cancel.ThrowIfCancellationRequested();
                if (prepareForRestart)
                    File.WriteAllText(Path.Combine(root, PendingFile),
                        JsonSerializer.Serialize(new PendingPack(source, custom ? result.Path : null)));
                else DeleteFileIfPresent(Path.Combine(root, PendingFile));
                this.ReplaceDirectory(root, destination, backup);
                return InstallResult.Succeeded(custom ? result.Path : destination, result.Manifest!, !custom);
            }
            finally
            {
                this.TryCleanup(staging);
                if (IsRemote(source)) this.TryDeleteDownload();
            }
        }
        catch (OperationCanceledException)
        {
            return InstallResult.Failed("The install was cancelled before activation.");
        }
        catch (Exception error)
        {
            this.log.Error(error, "Could not install the language pack from {Source}.", source);
            return InstallResult.Failed(error.Message);
        }
    }

    // Call only during game boot, before any resource hooks are installed.
    public InstallResult? ActivatePending(Action<string, string> applySelection)
    {
        try
        {
            using var installLock = this.LockInstallation();
            var pending = Path.Combine(this.configDirectory, PendingFolder);
            var backup = Path.Combine(this.configDirectory, BackupFolder);
            RecoverDirectory(this.InstalledPath, backup);
            RecoverDirectory(pending, Path.Combine(this.configDirectory, PendingBackupFolder));
            // An interrupted activation can leave its selection record in the installed folder.
            var root = File.Exists(Path.Combine(pending, PendingFile)) ? pending : this.InstalledPath;
            var selectionPath = Path.Combine(root, PendingFile);
            if (!File.Exists(selectionPath)) return null;
            var selection = JsonSerializer.Deserialize<PendingPack>(File.ReadAllText(selectionPath))
                ?? throw new InvalidDataException("Invalid pending pack selection.");
            if (string.IsNullOrWhiteSpace(selection.Source))
                throw new InvalidDataException("The pending pack has no source.");
            if (selection.Folder is { } folder)
            {
                if (!Path.IsPathFullyQualified(folder))
                    throw new InvalidDataException("The pending pack folder must be an absolute path.");
                this.RequireExternalSource(folder);
            }
            var result = selection.Folder is { } custom ? this.Adopt(custom) : ValidateArchive(root);
            if (!result.Success) return result;
            var activePath = selection.Folder ?? this.InstalledPath;
            void Apply()
            {
                applySelection(activePath, selection.Source);
            }
            if (selection.Folder is null && root != this.InstalledPath)
                this.ReplaceDirectory(root, this.InstalledPath, backup, Apply);
            else Apply();
            // Retain the selection record until configuration has been saved.
            try { DeleteFileIfPresent(Path.Combine(selection.Folder is null ? this.InstalledPath : root, PendingFile)); }
            catch (Exception error) { this.log.Warning(error, "Could not remove the completed pack selection."); }
            if (root == pending) this.TryCleanup(pending);
            return InstallResult.Succeeded(activePath, result.Manifest!, selection.Folder is null);
        }
        catch (Exception error)
        {
            this.log.Error(error, "Could not activate the pending language pack.");
            return InstallResult.Failed(error.Message);
        }
    }

    private void RequireExternalSource(string source)
    {
        var path = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var name in new[] { StagingFolder, BackupFolder, PendingFolder, PendingBackupFolder, DownloadFile })
        {
            var reserved = Path.GetFullPath(Path.Combine(this.configDirectory, name));
            if (path.Equals(reserved, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(reserved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a pack outside the installation work folders.");
        }
    }

    private sealed record PendingPack(string Source, string? Folder);

    private static void Extract(
        string archivePath, string destination, IProgress<InstallProgress>? progress, CancellationToken cancel)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);

        var total = archive.Entries.Count;
        var done = 0;

        foreach (var entry in archive.Entries)
        {
            cancel.ThrowIfCancellationRequested();

            done++;

            // A directory entry, which has no content and only needs to exist.
            if (entry.Name.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The archive tries to write outside the install folder ('{entry.FullName}'). Refusing it.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);

            // Often enough to move, rarely enough not to spend the install marshalling reports.
            if (done % 200 == 0 || done == total)
            {
                progress?.Report(InstallProgress.Working("Unpacking", done, total));
            }
        }
    }

    private InstallResult Adopt(string folder)
    {
        var (manifest, error) = PackManifest.Read(folder);
        return manifest is null
            ? InstallResult.Failed(error ?? "That folder is not a language pack.")
            : InstallResult.Succeeded(folder, manifest, unpacked: false);
    }

    private static InstallResult ValidateArchive(string folder)
    {
        var root = folder;
        var (manifest, error) = PackManifest.Read(root);
        if (manifest is null)
        {
            var nested = Directory.GetDirectories(root);
            if (nested.Length != 1 || PackManifest.Read(nested[0]).Manifest is not { } inner)
                return InstallResult.Failed(error ?? "The archive has no pack manifest.");
            root = nested[0];
            manifest = inner;
        }
        return Directory.EnumerateFiles(root, "*.exd", SearchOption.AllDirectories).Any()
            ? InstallResult.Succeeded(root, manifest, true)
            : InstallResult.Failed("The archive holds a manifest but no pages.");
    }

    // Do not cancel between the folder swap and configuration commit.
    private void ReplaceDirectory(string source, string destination, string backup, Action? apply = null)
    {
        DeleteIfPresent(backup);
        var existed = Directory.Exists(destination);
        if (existed) Directory.Move(destination, backup);
        var moved = false;
        try
        {
            Directory.Move(source, destination);
            moved = true;
            apply?.Invoke();
        }
        catch
        {
            if (moved) Directory.Move(destination, source);
            if (existed) Directory.Move(backup, destination);
            throw;
        }
        this.TryCleanup(backup);
    }

    private async Task<string> DownloadAsync(
        string url, IProgress<InstallProgress>? progress, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(DownloadTimeout);
        cancel = deadline.Token;
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var target = Path.Combine(this.configDirectory, DownloadFile);
        DeleteFileIfPresent(target);

        this.log.Information("Downloading a language pack from {Url}.", url);
        progress?.Report(InstallProgress.Working("Connecting"));

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using (var http = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        await using (var file = File.Create(target))
        {
            // Copied by hand rather than with CopyToAsync, only so the bytes can be counted.
            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await http.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                received += read;
                progress?.Report(InstallProgress.Downloading(received, total));
            }
        }

        RequireArchive(target, url);
        return target;
    }

    private static void RequireArchive(string file, string url)
    {
        Span<byte> magic = stackalloc byte[2];

        using (var stream = File.OpenRead(file))
        {
            if (stream.Read(magic) == magic.Length && magic[0] == (byte)'P' && magic[1] == (byte)'K')
            {
                return;
            }
        }

        var size = new FileInfo(file).Length;
        DeleteFileIfPresent(file);

        throw new InvalidDataException(
            $"{url} did not return a zip file ({size:N0} bytes of something else, most likely a web "
            + "page). Share links from WeTransfer, Google Drive and Dropbox give you a page rather "
            + "than the file; you need an address that downloads the archive directly.");
    }

    public async Task<UpdateStatus> CheckForUpdateAsync(PackManifest? installed, CancellationToken cancel = default,
        string? runningGame = null)
    {
        if (installed?.UpdateUrl is not { Length: > 0 } updateUrl)
        {
            return UpdateStatus.NotDeclared;
        }

        if (!IsRemote(updateUrl))
        {
            return UpdateStatus.Unreachable($"'{updateUrl}' is not an http address.");
        }

        try
        {
            var published = await ReadManifestAsync(updateUrl, cancel).ConfigureAwait(false);

            if (published?.TranslationVersion is not { Length: > 0 } latest)
            {
                return UpdateStatus.Unreachable("what is published there is not a pack manifest.");
            }

            var targetsCurrentGame = !string.IsNullOrWhiteSpace(runningGame) &&
                string.Equals(published.GameVersion, runningGame, StringComparison.Ordinal) &&
                !string.Equals(installed.GameVersion, runningGame, StringComparison.Ordinal);
            return targetsCurrentGame || string.CompareOrdinal(latest, installed.TranslationVersion ?? string.Empty) > 0
                ? UpdateStatus.Available(published)
                : new UpdateStatus(UpdateState.UpToDate, published, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            this.log.Warning("Could not reach {Url} to check for a newer pack: {Message}", updateUrl, e.Message);
            return UpdateStatus.Unreachable(e.Message);
        }
    }

    public async Task<PackManifest?> FetchManifestAsync(string url, CancellationToken cancel = default)
    {
        try
        {
            var manifest = await ReadManifestAsync(url, cancel).ConfigureAwait(false);
            return manifest?.TranslationVersion is { Length: > 0 } ? manifest : null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            this.log.Warning("Could not read the manifest at {Url}: {Message}", url, e.Message);
            return null;
        }
    }

    private static async Task<PackManifest?> ReadManifestAsync(string url, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(ManifestTimeout);
        cancel = deadline.Token;
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await using var stream = await client.GetStreamAsync(url, cancel).ConfigureAwait(false);

        return await System.Text.Json.JsonSerializer
            .DeserializeAsync<PackManifest>(stream, cancellationToken: cancel).ConfigureAwait(false);
    }

    // The lock also prevents an unloading instance from sharing staging files with its replacement.
    private FileStream LockInstallation() => new(Path.Combine(this.configDirectory, "pack.install.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static void RecoverDirectory(string destination, string backup)
    {
        if (!Directory.Exists(backup)) return;
        if (!Directory.Exists(destination)) Directory.Move(backup, destination);
        else DeleteIfPresent(backup);
    }

    private void TryDeleteDownload()
    {
        try { DeleteFileIfPresent(Path.Combine(this.configDirectory, DownloadFile)); }
        catch (Exception error) { this.log.Warning(error, "Could not remove the pack download."); }
    }

    private void TryCleanup(string directory)
    {
        try { DeleteIfPresent(directory); }
        catch (Exception error) { this.log.Warning(error, "Could not remove pack staging or backup files."); }
    }

    private static void DeleteIfPresent(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DeleteFileIfPresent(string file)
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}

internal readonly record struct InstallProgress(string Label, float? Fraction, string Detail)
{
    public static InstallProgress Working(string label) => new(label, null, string.Empty);

    public static InstallProgress Working(string label, int done, int total) =>
        new(label, total > 0 ? (float)done / total : null, $"{done:N0} / {total:N0} files");

    public static InstallProgress Downloading(long received, long? total)
    {
        var got = received / 1024d / 1024d;
        return total is > 0
            ? new("Downloading", (float)((double)received / total.Value), $"{got:N1} / {total.Value / 1024d / 1024d:N1} MB")
            : new("Downloading", null, $"{got:N1} MB");
    }
}

internal readonly record struct UpdateStatus(UpdateState State, PackManifest? Published, string? Error)
{
    public static readonly UpdateStatus NotDeclared = new(UpdateState.NotDeclared, null, null);

    public static readonly UpdateStatus UpToDate = new(UpdateState.UpToDate, null, null);

    public static UpdateStatus Available(PackManifest published) => new(UpdateState.Available, published, null);

    public static UpdateStatus Unreachable(string error) => new(UpdateState.Unreachable, null, error);
}

internal enum UpdateState
{
    Checking,

    NotDeclared,

    UpToDate,

    Available,

    Unreachable,
}

internal readonly record struct InstallResult(
    bool Success, string Path, PackManifest? Manifest, bool Unpacked, string? Error)
{
    public static InstallResult Succeeded(string path, PackManifest manifest, bool unpacked) =>
        new(true, path, manifest, unpacked, null);

    public static InstallResult Failed(string error) => new(false, string.Empty, null, false, error);
}
