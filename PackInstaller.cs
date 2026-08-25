using System.IO.Compression;
using System.Net.Http;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Puts a language pack on disk, from a folder, a zip file or a URL.
/// </summary>
/// <remarks>
///     <para>
///         <b>Installing is a separate act from serving.</b> The redirection has to be in place
///         before the client's first Excel read, a second after the plugin loads; ordinarily nothing
///         here runs during startup at all — the user presses a button, waits, and restarts.
///     </para>
///     <para>
///         <b>The exception is narrow.</b> With <c>AutoUpdatePack</c> on, the install runs during the
///         plugin's own construction so the session needs no restart. That works only because Dalamud
///         can be asked to hold the game's boot, a setting the player owns and the plugin checks
///         rather than gambles on. Everything reachable from there takes a
///         <see cref="CancellationToken" />: a stalled download during startup is somebody's game not
///         starting.
///     </para>
///     <para>
///         <b>A pack is published as a pair</b> — <c>whatever.zip</c> and a <c>whatever.json</c> copy
///         of its manifest beside it, so asking whether a newer generation exists costs two kilobytes
///         instead of the whole archive. <b>A folder source is used where it lies</b>: copying it
///         would leave a second copy no build writes to.
///     </para>
/// </remarks>
internal sealed class PackInstaller
{
    /// <summary>Where an unpacked archive lives: one installed pack, replaced wholesale.</summary>
    private const string InstalledFolder = "pack";

    /// <summary>Where it is unpacked <em>to</em> before replacing the installed one.</summary>
    /// <remarks>
    ///     Unpacking over the live folder would leave a mixture of two generations behind any failure
    ///     — a half-written pack that still holds a manifest and still loads, with every page
    ///     individually valid. Staging then swapping leaves a failed install exactly as it was.
    /// </remarks>
    private const string StagingFolder = "pack.staging";

    /// <summary>The archive on its way to <see cref="StagingFolder" />, and the largest thing written here.</summary>
    private const string DownloadFile = "pack.download.zip";

    /// <summary>Generous, because a language pack is tens of megabytes on somebody's home line.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Short, because nobody is waiting on it and it must never hold anything up.</summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);

    private readonly IPluginLog log;
    private readonly string configDirectory;

    public PackInstaller(IPluginLog log, string configDirectory)
    {
        this.log = log;
        this.configDirectory = configDirectory;
    }

    /// <summary>Where a pack installed from an archive ends up.</summary>
    public string InstalledPath => Path.Combine(this.configDirectory, InstalledFolder);

    /// <summary>True when <paramref name="source" /> names something to fetch over the network.</summary>
    public static bool IsRemote(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsArchive(string source) =>
        source.Trim().EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Resolves a source to a folder the redirector can serve, unpacking it if it is an archive.
    /// </summary>
    /// <remarks>
    ///     Deliberately not incremental: a pack half of one generation and half of another is exactly
    ///     what nothing downstream can detect. Cancellable only up to <see cref="Commit" />, which is
    ///     the safe half by construction — everything before it happens in the staging folder. Nobody
    ///     cancels by hand; the token is how the startup path puts a ceiling on a download.
    /// </remarks>
    public async Task<InstallResult> InstallAsync(
        string source, IProgress<InstallProgress>? progress = null, CancellationToken cancel = default)
    {
        source = source.Trim();

        if (source.Length == 0)
        {
            return InstallResult.Failed("Nothing to install.");
        }

        try
        {
            if (!IsRemote(source) && !IsArchive(source))
            {
                return Directory.Exists(source)
                    ? this.Adopt(source)
                    : InstallResult.Failed($"No folder at '{source}'.");
            }

            var staging = Path.Combine(this.configDirectory, StagingFolder);
            DeleteIfPresent(staging);

            if (IsRemote(source))
            {
                var downloaded = await this.DownloadAsync(source, progress, cancel).ConfigureAwait(false);
                try
                {
                    Extract(downloaded, staging, progress, cancel);
                }
                finally
                {
                    // Its purpose ends the moment it is unpacked, and it is the largest thing this
                    // plugin ever writes.
                    DeleteFileIfPresent(downloaded);
                }
            }
            else if (File.Exists(source))
            {
                Extract(source, staging, progress, cancel);
            }
            else
            {
                return InstallResult.Failed($"No archive at '{source}'.");
            }

            progress?.Report(InstallProgress.Working("Checking the pack"));
            return this.Commit(staging, source);
        }
        catch (OperationCanceledException)
        {
            // An outcome rather than a rethrow: every caller only needs to know the previous pack is
            // still there, which the staging folder guarantees and this sweeps up after.
            DeleteIfPresent(Path.Combine(this.configDirectory, StagingFolder));
            DeleteFileIfPresent(Path.Combine(this.configDirectory, DownloadFile));
            this.log.Warning("Installing the language pack from '{Source}' was abandoned.", source);
            return InstallResult.Failed("The install was abandoned before anything was replaced.");
        }
        catch (Exception e)
        {
            this.log.Error(e, "Installing the language pack from '{Source}' failed.", source);
            return InstallResult.Failed(e.Message);
        }
    }

    /// <summary>
    ///     Unpacks an archive entry by entry so the caller can say how far along it is.
    /// </summary>
    /// <remarks>
    ///     <c>ZipFile.ExtractToDirectory</c> would be one line, and was, but a pack is thousands of
    ///     files and on a slow disk the silence reads as a hang. Doing it by hand means doing the
    ///     safety check by hand: an entry named <c>..\..\something.dll</c> would otherwise be written
    ///     outside the target, so every destination is resolved and required to sit under the root.
    /// </remarks>
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
            // Between entries, never inside one: a half-written .exd is a page the redirector would
            // serve as if it were whole.
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

    /// <summary>
    ///     Accepts a folder where it already is, once it looks like a pack.
    /// </summary>
    /// <remarks>
    ///     The path a pack is built to rather than installed to, which is how this project's own
    ///     corpus is used. Checking the manifest here means a mistyped path is reported while somebody
    ///     is looking at the window, not silently at the next start.
    /// </remarks>
    private InstallResult Adopt(string folder)
    {
        var (manifest, error) = PackManifest.Read(folder);
        return manifest is null
            ? InstallResult.Failed(error ?? "That folder is not a language pack.")
            : InstallResult.Succeeded(folder, manifest, unpacked: false);
    }

    /// <summary>
    ///     Checks what was unpacked, then puts it in place of the pack that was installed before.
    /// </summary>
    /// <remarks>
    ///     Validated before the swap, never after: once the previous pack is gone there is nothing to
    ///     fall back to. An archive zipped one level too high is the likeliest publisher mistake and
    ///     is recovered rather than reported, being unambiguous.
    /// </remarks>
    private InstallResult Commit(string staging, string source)
    {
        var root = staging;
        var (manifest, error) = PackManifest.Read(root);

        if (manifest is null)
        {
            var nested = Directory.GetDirectories(staging);
            if (nested.Length == 1 && PackManifest.Read(nested[0]).Manifest is { } inner)
            {
                root = nested[0];
                manifest = inner;
            }
            else
            {
                DeleteIfPresent(staging);
                return InstallResult.Failed(error ?? $"{PackManifest.FileName} is not in that archive.");
            }
        }

        if (!Directory.EnumerateFiles(root, "*.exd", SearchOption.AllDirectories).Any())
        {
            DeleteIfPresent(staging);
            return InstallResult.Failed("That archive holds a manifest but no pages.");
        }

        var installed = this.InstalledPath;
        DeleteIfPresent(installed);
        Directory.Move(root, installed);

        // Only when the archive was nested: the wrapper directory is left behind by the move above.
        DeleteIfPresent(staging);

        this.log.Information(
            "Installed '{Pack}' ({Version}) from {Source}.",
            manifest.DisplayName,
            manifest.TranslationVersion ?? "unversioned",
            source);

        return InstallResult.Succeeded(installed, manifest, unpacked: true);
    }

    private async Task<string> DownloadAsync(
        string url, IProgress<InstallProgress>? progress, CancellationToken cancel)
    {
        using var client = new HttpClient { Timeout = DownloadTimeout };

        var target = Path.Combine(this.configDirectory, DownloadFile);
        DeleteFileIfPresent(target);

        this.log.Information("Downloading a language pack from {Url}.", url);
        progress?.Report(InstallProgress.Working("Connecting"));

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Null when the server does not say, which is legal and happens with chunked responses. The
        // bar then has no fraction and the byte count carries the reassurance alone.
        var total = response.Content.Headers.ContentLength;

        await using (var http = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
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

    /// <summary>
    ///     Refuses anything that is not a zip, before the unpacker describes it in its own words.
    /// </summary>
    /// <remarks>
    ///     The likeliest mistake with this feature by a wide margin: a file-sharing link is a
    ///     <em>page</em>, not a file — WeTransfer, Google Drive and Dropbox all answer with HTML.
    ///     Left alone the unpacker says "End of Central Directory record could not be found", which
    ///     is true and useless; two bytes of checking buys a sentence naming the real problem.
    /// </remarks>
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
            $"{url} did not return a zip file ({size:N0} bytes of something else — most likely a web "
            + "page). Share links from WeTransfer, Google Drive and Dropbox give you a page rather "
            + "than the file; you need an address that downloads the archive directly.");
    }

    /// <summary>
    ///     Asks the publisher whether a newer generation exists, without downloading the pack.
    /// </summary>
    /// <remarks>
    ///     Fetches the manifest at the pack's own <see cref="PackManifest.UpdateUrl" /> — kilobytes
    ///     against the archive's twenty megabytes, which is why an automatic check is acceptable.
    ///     Returns null for every kind of "no", failures included: no address, a host that is down, a
    ///     captive portal serving HTML. The cost of being wrong is somebody carrying on with a pack
    ///     that works.
    /// </remarks>
    public async Task<UpdateStatus> CheckForUpdateAsync(PackManifest? installed, CancellationToken cancel = default)
    {
        // Silence, and no complaint: a pack that declares no address has promised nothing, which is a
        // legitimate way to publish one.
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
            using var client = new HttpClient { Timeout = ManifestTimeout };
            await using var stream = await client.GetStreamAsync(updateUrl, cancel).ConfigureAwait(false);

            var published = await System.Text.Json.JsonSerializer
                .DeserializeAsync<PackManifest>(stream, cancellationToken: cancel).ConfigureAwait(false);

            if (published?.TranslationVersion is not { Length: > 0 } latest)
            {
                // Answered, but with something that is not a manifest — a captive portal or an error
                // page. A broken promise like any other.
                return UpdateStatus.Unreachable("what is published there is not a pack manifest.");
            }

            // Ordinal, not a version parse. The stamp is yyyy.MM.dd.HHmm, which sorts correctly as
            // text and has no meaning as a number.
            return string.CompareOrdinal(latest, installed.TranslationVersion ?? string.Empty) > 0
                ? UpdateStatus.Available(published)
                : UpdateStatus.UpToDate;
        }
        catch (Exception e)
        {
            this.log.Warning("Could not reach {Url} to check for a newer pack: {Message}", updateUrl, e.Message);
            return UpdateStatus.Unreachable(e.Message);
        }
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

/// <summary>How far along an install is, for the window to draw.</summary>
/// <param name="Label">What is happening, in the user's terms.</param>
/// <param name="Fraction">0 to 1, or null when the total is not known and no bar can be honest.</param>
/// <param name="Detail">The numbers behind the bar, or empty.</param>
internal readonly record struct InstallProgress(string Label, float? Fraction, string Detail)
{
    public static InstallProgress Working(string label) => new(label, null, string.Empty);

    public static InstallProgress Working(string label, int done, int total) =>
        new(label, total > 0 ? (float)done / total : null, $"{done:N0} / {total:N0} files");

    /// <remarks>
    ///     Megabytes rather than bytes: the number is for a person, and a nine-digit count moving too
    ///     fast to read reassures nobody.
    /// </remarks>
    public static InstallProgress Downloading(long received, long? total)
    {
        var got = received / 1024d / 1024d;
        return total is > 0
            ? new("Downloading", (float)((double)received / total.Value), $"{got:N1} / {total.Value / 1024d / 1024d:N1} MB")
            : new("Downloading", null, $"{got:N1} MB");
    }
}

/// <summary>What came of asking whether a newer pack exists.</summary>
/// <remarks>
///     Four outcomes rather than a nullable manifest, because two of them are silence for opposite
///     reasons: a pack naming no address has promised nothing, while one that names an address and
///     does not answer has stopped delivering corrections.
/// </remarks>
internal readonly record struct UpdateStatus(UpdateState State, PackManifest? Published, string? Error)
{
    public static readonly UpdateStatus NotDeclared = new(UpdateState.NotDeclared, null, null);

    public static readonly UpdateStatus UpToDate = new(UpdateState.UpToDate, null, null);

    public static UpdateStatus Available(PackManifest published) => new(UpdateState.Available, published, null);

    public static UpdateStatus Unreachable(string error) => new(UpdateState.Unreachable, null, error);
}

internal enum UpdateState
{
    /// <summary>Nothing has been asked yet. <b>Deliberately first, so it is the default.</b></summary>
    /// <remarks>
    ///     The check takes as long as a web request. Any other default would state a conclusion during
    ///     the seconds before there is one — and for a pack that does publish updates, the opposite
    ///     of the truth.
    /// </remarks>
    Checking,

    /// <summary>The pack names no update address, so it cannot tell anyone it has changed.</summary>
    NotDeclared,

    /// <summary>Asked and answered: what is published is what is installed.</summary>
    UpToDate,

    /// <summary>A newer generation is published.</summary>
    Available,

    /// <summary>An address was declared and did not answer with a manifest.</summary>
    Unreachable,
}

/// <param name="Success">Whether there is now a pack on disk to serve.</param>
/// <param name="Path">Where it is. Empty on failure.</param>
/// <param name="Manifest">What it says about itself. Null on failure.</param>
/// <param name="Unpacked">True when an archive was expanded, false when a folder was used in place.</param>
/// <param name="Error">Why not, when not.</param>
internal readonly record struct InstallResult(
    bool Success, string Path, PackManifest? Manifest, bool Unpacked, string? Error)
{
    public static InstallResult Succeeded(string path, PackManifest manifest, bool unpacked) =>
        new(true, path, manifest, unpacked, null);

    public static InstallResult Failed(string error) => new(false, string.Empty, null, false, error);
}
