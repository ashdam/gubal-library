using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Append-only diagnostic: records the normalized key of every dialogue line that had no
///     translation.
/// </summary>
/// <remarks>
///     <para>
///         This is not a corpus generator and makes no network calls. Its job is to answer "why didn't
///         that line translate?" — the recorded key is exactly what the lookup used, so diffing it
///         against the <c>en</c> field in the corpus shows the divergence immediately (usually whitespace,
///         a payload artifact, or a macro the offline pipeline didn't tokenize).
///     </para>
///     <para>
///         It also bootstraps testing: paste a recorded key into the corpus as an <c>en</c> value and you
///         have a guaranteed-matching entry.
///     </para>
/// </remarks>
internal sealed class MissLog(IPluginLog log, string path) : IDisposable
{
    private const int MaxRecorded = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Keep accents and quotes readable rather than \u-escaped — this file is meant to be eyeballed.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private bool capacityWarned;

    public int Count => this.seen.Count;

    public string Path => path;

    public void Dispose()
    {
        this.seen.Clear();
    }

    /// <summary>Records a miss, deduplicated in memory so a repeated line writes only once.</summary>
    /// <param name="key">The normalized string the lookup actually used.</param>
    /// <param name="raw">
    ///     The untouched string read out of the game, before normalization. Recorded separately so a
    ///     divergence introduced *by* normalization is visible rather than hidden — without this, a rule
    ///     that eats something it shouldn't looks identical to a genuinely missing entry.
    /// </param>
    /// <param name="speaker">Speaker name, for context when reading the file.</param>
    public void Record(string key, string raw, string speaker)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        // Checked before the insert, not after. Adding first and then bailing still grows the set on
        // every new line for the rest of the session, so the cap stopped the writes but not the
        // memory it was there to bound.
        if (this.seen.Count >= MaxRecorded)
        {
            if (!this.capacityWarned)
            {
                this.capacityWarned = true;
                log.Warning("Miss log reached {Max} distinct entries; stopping writes.", MaxRecorded);
            }

            return;
        }

        if (!this.seen.Add(key))
        {
            return;
        }

        try
        {
            // Only carry `raw` when normalization actually changed something — otherwise it is noise.
            var line = JsonSerializer.Serialize(
                new Miss(
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    key,
                    string.Equals(key, raw, StringComparison.Ordinal) ? null : raw,
                    speaker),
                JsonOptions);
            File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to append to miss log {Path}", path);
        }
    }

    public void Reset()
    {
        this.seen.Clear();
        this.capacityWarned = false;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to delete miss log {Path}", path);
        }
    }

    /// <summary>
    ///     A recorded miss. <see cref="Time" /> matters more than it looks: without it there is no way
    ///     to tell a stale miss from before a reload apart from a live one, which already cost one
    ///     round of misdiagnosis.
    /// </summary>
    private sealed record Miss(string Time, string Key, string? Raw, string Speaker);
}
