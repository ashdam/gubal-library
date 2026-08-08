using System.IO.Compression;
using System.Net.Http;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Puts a language pack on disk, from a folder, a zip file or a URL.
/// </summary>
/// <remarks>
///     <para>
///         <b>Installing is a separate act from serving, and that separation is load-bearing.</b> The
///         redirection has to be in place before the client's first Excel read, which is about two
///         seconds after plugins load, and this plugin holds the game's boot while it starts. Fetching
///         twenty megabytes and unpacking three thousand files there would spend that margin and a
///         great deal more, turning a working design into a frozen loading screen. So nothing here
///         ever runs during startup: the user presses a button, waits, and restarts.
///     </para>
///     <para>
///         <b>A pack is published as a pair.</b> <c>whatever.zip</c> holds the pages and their
///         <c>gubal-manifest.json</c>; <c>whatever.json</c> sits beside it and is a copy of that same
///         manifest. Asking whether a newer generation exists then costs two kilobytes instead of the
///         whole archive, which is what makes an automatic check acceptable to run at all.
///     </para>
///     <para>
///         <b>A folder source is used where it lies.</b> Copying it into the plugin's own directory
///         would produce a second copy that no build writes to, and the first person to rebuild would
///         be testing yesterday's pack while believing otherwise. Only archives are unpacked, because
///         only archives have to be.
///     </para>
/// </remarks>
internal sealed class PackInstaller
{
    /// <summary>Where an unpacked archive lives: one installed pack, replaced wholesale.</summary>
    private const string InstalledFolder = "pack";

    /// <summary>Where it is unpacked <em>to</em> before replacing the installed one.</summary>
    /// <remarks>
    ///     Unpacking over the live folder would leave a mixture of two generations behind any failure
    ///     — a half-written pack that still holds a manifest and still loads. Nothing downstream can
    ///     detect that: every page is individually valid, so the game draws yesterday's Spanish on
    ///     today's rows in whichever sheets did not get replaced. Staging then swapping makes a failed
    ///     install leave the previous pack exactly as it was.
    /// </remarks>
    private const string StagingFolder = "pack.staging";

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
    ///     Deliberately not cancellable and deliberately not incremental. An install is a few seconds
    ///     of work that a person asked for and is watching, and the states that a resumable one could
    ///     leave behind — a pack that is half of one generation and half of another — are precisely
    ///     the ones nothing downstream can detect.
    /// </remarks>
    public async Task<InstallResult> InstallAsync(string source, IProgress<InstallProgress>? progress = null)
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
                var downloaded = await this.DownloadAsync(source, progress).ConfigureAwait(false);
                try
                {
                    Extract(downloaded, staging, progress);
                }
                finally
                {
                    // The archive has served its purpose the moment it is unpacked, and it is the
                    // largest thing this plugin ever writes.
                    DeleteFileIfPresent(downloaded);
                }
            }
            else if (File.Exists(source))
            {
                Extract(source, staging, progress);
            }
            else
            {
                return InstallResult.Failed($"No archive at '{source}'.");
            }

            progress?.Report(InstallProgress.Working("Checking the pack"));
            return this.Commit(staging, source);
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
    ///     <para>
    ///         <c>ZipFile.ExtractToDirectory</c> would be one line, and was, but it is opaque: a pack
    ///         is thousands of files and on a slow disk the wait is long enough that silence reads as
    ///         a hang.
    ///     </para>
    ///     <para>
    ///         Doing it by hand means doing the safety check by hand too. An archive can name an entry
    ///         like <c>..\..\something.dll</c>, and an extractor that simply joins paths will happily
    ///         write outside the directory it was given. Every destination is resolved and required to
    ///         sit under the target root before anything is written.
    ///     </para>
    /// </remarks>
    private static void Extract(string archivePath, string destination, IProgress<InstallProgress>? progress)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);

        var total = archive.Entries.Count;
        var done = 0;

        foreach (var entry in archive.Entries)
        {
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

            // Every few hundred files: often enough to move, rarely enough not to spend the install
            // marshalling progress reports.
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
    ///     corpus is used: the generator writes a folder and the plugin reads it. Checking the
    ///     manifest here rather than later means a mistyped path is reported while somebody is
    ///     looking at the window, not silently at the next start.
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
    ///     <para>
    ///         Validated before the swap, never after. Once the previous pack is gone there is nothing
    ///         to fall back to, so an archive that turns out to hold the wrong thing has to be caught
    ///         while the old one is still there.
    ///     </para>
    ///     <para>
    ///         An archive zipped one level too high is the likeliest mistake a publisher makes and it
    ///         is worth recovering from rather than reporting: it unpacks to a single folder holding
    ///         the real pack, which is unambiguous enough to just use.
    ///     </para>
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

    private async Task<string> DownloadAsync(string url, IProgress<InstallProgress>? progress)
    {
        using var client = new HttpClient { Timeout = DownloadTimeout };

        var target = Path.Combine(this.configDirectory, "pack.download.zip");
        DeleteFileIfPresent(target);

        this.log.Information("Downloading a language pack from {Url}.", url);
        progress?.Report(InstallProgress.Working("Connecting"));

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Null when the server does not say, which is legal and happens with chunked responses. The
        // bar then has no fraction to show and the byte count carries the reassurance on its own.
        var total = response.Content.Headers.ContentLength;

        await using (var http = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var file = File.Create(target))
        {
            // Copied by hand rather than with CopyToAsync, only so that the bytes can be counted.
            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await http.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                received += read;
                progress?.Report(InstallProgress.Downloading(received, total));
            }
        }

        RequireArchive(target, url);
        return target;
    }

    /// <summary>
    ///     Refuses anything that is not a zip, before the unpacker gets to describe it in its own words.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the likeliest mistake anybody makes with this feature, by a wide margin: a
    ///         file-sharing link is a <em>page</em>, not a file. WeTransfer, Google Drive and Dropbox
    ///         share URLs all answer with HTML — sometimes a consent screen, sometimes a login, often
    ///         a 404 to anything without a browser session — and none of them hand over the archive.
    ///     </para>
    ///     <para>
    ///         Left to itself the unpacker says "End of Central Directory record could not be found",
    ///         which is precisely true and tells the reader nothing they can act on. Two bytes of
    ///         checking buys a sentence that names the actual problem.
    ///     </para>
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
    ///     <para>
    ///         Fetches the manifest at the installed pack's own <see cref="PackManifest.UpdateUrl" />,
    ///         which is a few kilobytes rather than the twenty megabytes of the archive — and that
    ///         disproportion is the whole reason an automatic check is acceptable at all.
    ///     </para>
    ///     <para>
    ///         Returns null for every kind of "no", including every kind of failure. A pack that
    ///         declares no address, a host that is down, a captive portal serving an HTML page where
    ///         JSON should be — none of those are the user's problem and none justify a message. The
    ///         consequence of being wrong here is that somebody carries on using a pack that works.
    ///     </para>
    /// </remarks>
    public async Task<UpdateStatus> CheckForUpdateAsync(PackManifest? installed)
    {
        // Silence, and no complaint. A pack that declares no address has promised nothing, which is a
        // legitimate way to publish one — a test build, or an author with nowhere to host a manifest.
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
            await using var stream = await client.GetStreamAsync(updateUrl).ConfigureAwait(false);

            var published = await System.Text.Json.JsonSerializer
                .DeserializeAsync<PackManifest>(stream).ConfigureAwait(false);

            if (published?.TranslationVersion is not { Length: > 0 } latest)
            {
                // Answered, but with something that is not a manifest. A captive portal or an error
                // page dressed as HTML lands here, and it is a broken promise like any other.
                return UpdateStatus.Unreachable("what is published there is not a pack manifest.");
            }

            // Ordinal, not a version parse. The stamp is yyyy.MM.dd.HHmm, which sorts correctly as
            // text and has no meaning as a number; parsing it would only invent failure modes.
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
    ///     Reported in megabytes rather than bytes because the number is for a person, and a
    ///     nine-digit byte count moving too fast to read reassures nobody.
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
///     reasons and the difference is the user's to know. A pack that names no address has promised
///     nothing. A pack that names one and does not answer has stopped delivering corrections, and
///     from the player's chair that is indistinguishable from a translation nobody is working on.
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
    /// <summary>
    ///     Nothing has been asked yet. <b>Deliberately first, so it is the default.</b>
    /// </summary>
    /// <remarks>
    ///     The check runs on a background task and takes as long as a web request. Any other value
    ///     here would mean the window states a conclusion during the seconds before there is one —
    ///     and the conclusion it would state, for a pack that does publish updates, is the opposite
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
