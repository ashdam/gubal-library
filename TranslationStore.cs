using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     Read-only, in-memory translation lookup loaded once from the corpus file.
/// </summary>
/// <remarks>
///     Deliberately not a database. Echoglossian needs SQLite because it writes constantly; a
///     pre-translated artifact never writes, and its own hot paths already preload whole tables into
///     dictionaries. We keep that half and drop the rest.
/// </remarks>
internal sealed class TranslationStore(IPluginLog log, ISeStringEvaluator evaluator)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Ordinal throughout. Culture-sensitive or case-insensitive comparison here would be a bug: the
    // key must match the game's bytes exactly, modulo the normalization in TextKey.
    private Dictionary<string, string> entries = new(StringComparer.Ordinal);
    private Dictionary<string, string> npcNames = new(StringComparer.Ordinal);

    /// <summary>
    ///     The same translations again, keyed by conversation as well as text.
    /// </summary>
    /// <remarks>
    ///     Consulted first, and it is what makes identical English readable two different ways. 4,407
    ///     of the corpus's 5,378 repeated English lines occur in more than one quest; keyed on text
    ///     alone they collapse onto whichever translation happened to load first, and the rest are
    ///     unreachable. <see cref="EventContext" /> supplies the conversation at lookup time.
    ///     <para>
    ///         Holding both indexes roughly doubles the key overhead but not the values — the strings
    ///         themselves are shared references, so only the dictionary entries are duplicated.
    ///     </para>
    /// </remarks>
    private Dictionary<string, string> scoped = new(StringComparer.Ordinal);

    /// <summary>
    ///     Entries whose key depends on the Eorzean clock, and the key each currently occupies.
    /// </summary>
    /// <remarks>
    ///     Greetings like "Good <c>&lt;if([gnum11&lt;12],…)&gt;</c>, adventurer" resolve against the
    ///     in-game hour, so a key built at load stops matching once the clock moves — and an Eorzean
    ///     hour is under three real minutes, so it stops matching almost immediately.
    ///     <para>
    ///         Re-evaluating just this subset costs about 2 ms, which is cheap enough to do
    ///         periodically. That is far simpler than the alternative of expanding each entry into one
    ///         variant per time of day, and it needs no way to force a value into a global parameter —
    ///         which the evaluator does not offer anyway.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     The corpus schema this build reads.
    /// </summary>
    /// <remarks>
    ///     A mismatch is only a warning, because loading anyway is usually right — an unknown future
    ///     version may well be readable. Schema 1 is the case where it is not: it named the entry
    ///     fields <c>en</c> and <c>es</c>, so this build parses such a file without error, matches
    ///     nothing in it, and loads zero entries. On screen that looks identical to a broken plugin.
    /// </remarks>
    private const int SupportedSchemaVersion = 2;

    /// <summary>How many unevaluable lines to name in the log before falling back to a tally.</summary>
    private const int MaxReportedFailures = 10;

    private List<TimeSensitiveEntry> timeSensitive = [];

    private long lastTimeRefreshTicks;

    public int Count => this.entries.Count;

    /// <summary>How many entries are also reachable by conversation-scoped key.</summary>
    public int ScopedCount => this.scoped.Count;

    public int NpcNameCount => this.npcNames.Count;

    public string LoadedFrom { get; private set; } = "(not loaded)";

    /// <summary>
    ///     Full path of the file actually loaded, or empty if none was.
    /// </summary>
    /// <remarks>
    ///     Kept alongside <see cref="LoadedFrom" />, which is only a display name. Callers need the
    ///     real path to answer "is this the bundled sample?" — and comparing display names would get
    ///     that wrong the moment someone names their own corpus the same thing.
    /// </remarks>
    public string LoadedPath { get; private set; } = string.Empty;

    public string? TargetLanguage { get; private set; }

    public string? GameVersion { get; private set; }

    /// <summary>
    ///     Which generation of the translation is loaded, stamped to the minute by the extractor.
    /// </summary>
    /// <remarks>
    ///     Reported because the alternative is guessing. The corpus is regenerated several times in a
    ///     working session, it is not distributed with the plugin, and its filename says nothing — so
    ///     without this there is no way to tell a stale copy from a current one, and "did my new
    ///     translation actually load?" has no answer. Null for a corpus written before the field existed.
    /// </remarks>
    public string? TranslationVersion { get; private set; }

    /// <summary>
    ///     Loads the first readable file from <paramref name="candidatePaths" />, in order. The config
    ///     directory is checked before the bundled sample so a large translated file can be swapped in
    ///     without rebuilding the plugin.
    /// </summary>
    public bool Load(IEnumerable<string> candidatePaths)
    {
        // Materialised: the caller passes a yield-return iterator and it is enumerated twice here.
        var paths = candidatePaths.ToArray();
        var primary = paths.FirstOrDefault(File.Exists);

        if (primary is not null)
        {
            try
            {
                this.LoadFiles([primary]);
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to load translation file {Path}", primary);
                return false;
            }
        }

        // Not an error state: with no corpus the plugin simply never injects and the game's own text
        // is left alone. Say where to put one rather than just reporting failure.
        log.Warning(
            "No translation file found. Nothing will be translated and all dialogue stays as the game "
            + "renders it. Place a corpus at one of: {Paths} — or set an explicit path in /gubal.",
            string.Join(" | ", paths));

        this.LoadedFrom = "(no translation file found)";
        this.LoadedPath = string.Empty;
        return false;
    }

    /// <summary>
    ///     Re-evaluates clock-dependent entries if enough time has passed, rekeying any that changed.
    /// </summary>
    /// <remarks>
    ///     Call this before a lookup. Throttled internally, so calling it on every dialogue line is
    ///     fine — the work only happens roughly once per Eorzean hour boundary.
    /// </remarks>
    public void RefreshTimeSensitive()
    {
        if (this.timeSensitive.Count == 0)
        {
            return;
        }

        // An Eorzean hour is ~2m55s of real time; checking every 30s catches every boundary without
        // doing the work more than a handful of times per hour.
        var now = Environment.TickCount64;
        if (now - this.lastTimeRefreshTicks < 30_000)
        {
            return;
        }

        this.lastTimeRefreshTicks = now;
        var rekeyed = 0;

        foreach (var entry in this.timeSensitive)
        {
            if (!this.TryEvaluate(entry.Source, out var resolved))
            {
                continue;
            }

            var key = TextKey.Normalize(resolved);
            if (key.Length == 0 || string.Equals(key, entry.CurrentKey, StringComparison.Ordinal))
            {
                continue;
            }

            // Only withdraw the old key if it is still ours. Another entry may have taken it, and
            // stealing it back would silently break that one instead.
            if (this.entries.TryGetValue(entry.CurrentKey, out var held)
                && string.Equals(held, entry.Value, StringComparison.Ordinal))
            {
                this.entries.Remove(entry.CurrentKey);
            }

            this.entries[key] = entry.Value;

            // Both indexes or neither. A scoped entry left under a stale hour keeps answering with
            // yesterday's greeting and, because the scoped index is consulted first, it would win over
            // the correctly rekeyed fallback — worse than not scoping at all.
            if (entry.Conversation is not null)
            {
                this.scoped.Remove(ScopedKey(entry.Conversation, entry.CurrentKey));
                this.scoped[ScopedKey(entry.Conversation, key)] = entry.Value;
            }

            entry.CurrentKey = key;
            rekeyed++;
        }

        if (rekeyed > 0)
        {
            // Information rather than Debug: this is the only externally visible sign the mechanism
            // works, and it cannot be observed by talking to an NPC unless you happen to find one of
            // the few whose line is clock-dependent.
            log.Information("Rekeyed {Count} clock-dependent entries after an Eorzean hour change.", rekeyed);
        }
    }

    /// <summary>
    ///     Looks up a line, preferring the translation written for this specific conversation.
    /// </summary>
    /// <param name="conversation">
    ///     The active quest's conversation id from <see cref="EventContext" />, or null outside a quest
    ///     scene. Null is ordinary — ambient chatter has no quest handler.
    /// </param>
    /// <param name="key">The normalized on-screen text.</param>
    /// <param name="translated">The Spanish line.</param>
    /// <remarks>
    ///     The fallback matters as much as the scoped hit. A conversation-only lookup would fail for
    ///     every ambient line and for any quest whose handler could not be read, turning a
    ///     previously-working translation into English. Scoping is an improvement layered over the text
    ///     join, never a replacement for it.
    /// </remarks>
    public bool TryGetTranslation(string? conversation, string key, out string translated)
    {
        if (conversation is not null
            && this.scoped.TryGetValue(ScopedKey(conversation, key), out var exact)
            && !string.IsNullOrEmpty(exact))
        {
            translated = exact;
            return true;
        }

        if (this.entries.TryGetValue(key, out var found) && !string.IsNullOrEmpty(found))
        {
            translated = found;
            return true;
        }

        translated = string.Empty;
        return false;
    }

    /// <summary>
    ///     Composite key for the scoped index.
    /// </summary>
    /// <remarks>
    ///     NUL as the separator because it is the one character that cannot occur in either half — a
    ///     printable separator risks a conversation id ending, or a line beginning, with it.
    /// </remarks>
    private static string ScopedKey(string conversation, string key)
    {
        return conversation + '\0' + key;
    }

    public bool TryGetNpcName(string englishName, out string translated)
    {
        if (!string.IsNullOrEmpty(englishName)
            && this.npcNames.TryGetValue(englishName, out var found)
            && !string.IsNullOrEmpty(found))
        {
            translated = found;
            return true;
        }

        translated = string.Empty;
        return false;
    }

    /// <summary>
    ///     Loads every file into one pair of indexes, replacing what was loaded before.
    /// </summary>
    /// <remarks>
    ///     Built once and assigned at the end rather than accumulated into the live dictionaries: a
    ///     reload that throws half way through would otherwise leave the plugin translating from a
    ///     corpus that is part old and part new, which is worse than either.
    /// </remarks>
    private void LoadFiles(string[] paths)
    {
        var built = new Dictionary<string, string>(StringComparer.Ordinal);
        var builtScoped = new Dictionary<string, string>(StringComparer.Ordinal);
        var timed = new List<TimeSensitiveEntry>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            this.LoadFile(path, built, builtScoped, timed, names);
        }

        this.entries = built;
        this.scoped = builtScoped;
        this.timeSensitive = timed;
        this.npcNames = names;
        this.lastTimeRefreshTicks = Environment.TickCount64;
        this.LoadedFrom = string.Join(" + ", paths.Select(Path.GetFileName));
        this.LoadedPath = paths.Length == 1 ? paths[0] : string.Empty;

        log.Information(
            "Loaded {Entries} entries ({Scoped} conversation-scoped) and {Names} NPC names from {Count} file(s): {Files}",
            this.entries.Count,
            this.scoped.Count,
            this.npcNames.Count,
            paths.Length,
            this.LoadedFrom);
    }

    private void LoadFile(
        string path,
        Dictionary<string, string> built,
        Dictionary<string, string> builtScoped,
        List<TimeSensitiveEntry> timed,
        Dictionary<string, string> names)
    {
        // Instrumentation, kept permanently: at full corpus scale the cost of loading is the whole
        // question, and "it feels slow" is not something you can act on. Cheap enough to always run.
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var readWatch = Stopwatch.StartNew();
        Stopwatch parseWatch;

        // Deserialize straight from the file stream. Reading into a string first would materialise the
        // whole corpus as UTF-16 — for an 85 MB file that is a ~170 MB allocation on the large object
        // heap, on top of the data we actually keep, and the LOH is not compacted by default.
        FileModel model;
        using (var stream = File.OpenRead(path))
        {
            readWatch.Stop();
            parseWatch = Stopwatch.StartNew();

            model = JsonSerializer.Deserialize<FileModel>(stream, JsonOptions)
                    ?? throw new InvalidDataException("Translation file deserialized to null.");
        }

        // Refused, not warned about and loaded anyway. A schema this build cannot read parses without
        // error, matches nothing, and yields zero entries — which on screen is indistinguishable from
        // a broken plugin, and sends whoever hits it looking in the wrong place. Schema 1 is exactly
        // that case: it named the entry fields "en" and "es" where this reads "source" and "target".
        if (model.SchemaVersion != SupportedSchemaVersion)
        {
            log.Error(
                "Refusing to load {Path}: it declares schemaVersion {Version} and this build reads "
                + "{Supported}. Nothing has been loaded. Regenerate the corpus with a matching extractor, "
                + "or install the plugin version that matches the corpus.",
                path,
                model.SchemaVersion,
                SupportedSchemaVersion);
            return;
        }

        var skippedEmpty = 0;
        var duplicates = 0;
        var strippedEmphasis = 0;
        var evaluated = 0;
        var evaluationFailures = 0;

        foreach (var entry in model.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target))
            {
                skippedEmpty++;
                continue;
            }

            // Every line goes through the game's own evaluator, with no test for whether it needs to.
            // Evaluating a string that holds no macros returns the string, so the check would only
            // buy speed, and any cheap version of it is wrong: searching for '<' catches an escaped
            // \<sigh>, which is literal text. Schema 1 answered this with a second field whose mere
            // presence was the signal, at the cost of shipping every macro line twice.
            //
            // What it does buy is exactness. The evaluator substitutes the player's name and takes
            // the same branch the game will, so the key is what appears on screen — where a flattened
            // rendering reads "Off you go now, ." against the game's "Off you go now, Mini.".
            if (!this.TryEvaluate(entry.Source, out var source))
            {
                // Dropped, not indexed under the raw macro text: that is a string the game will never
                // draw, so it could only match by accident, and an accident here injects the wrong
                // translation over whatever it collided with. It would also inflate the entry count
                // into a claim of coverage that does not exist. Leaving the game's own text on screen
                // is the right answer when the plugin cannot work out what the line says.
                evaluationFailures++;

                // Named, not just counted. A tally says 45 lines are silently not being translated
                // and gives you no way to find out which, and the per-macro detail was at Debug —
                // which is off, so in practice the question was unanswerable from a normal log.
                //
                // The source text rather than the gameKey: the runtime file carries no gameKey, and
                // the text is what you would search the corpus for anyway.
                if (evaluationFailures <= MaxReportedFailures)
                {
                    log.Warning(
                        "  will not evaluate, dropped: {Source}",
                        entry.Source.Length > 160 ? entry.Source[..160] + "…" : entry.Source);
                }

                continue;
            }

            evaluated++;

            var key = TextKey.Normalize(source);
            if (key.Length == 0)
            {
                skippedEmpty++;
                continue;
            }

            // Asterisks in the value would render literally: SetManagedString marshals plain UTF-8 and
            // does no macro parsing, so "*Orion*" appears on screen with the asterisks visible rather
            // than italicised. Verified in game. Strip them so a translator copying emphasis from the
            // English cannot produce visibly broken output.
            var value = entry.Target;
            if (value.Contains('*', StringComparison.Ordinal))
            {
                value = value.Replace("*", string.Empty, StringComparison.Ordinal);
                strippedEmphasis++;
            }

            // gnum11 is the Eorzean hour; those keys need re-evaluating as the clock advances. Tested
            // against the unevaluated source deliberately — by this point the evaluated form has the
            // hour already baked into it and says nothing about where it came from.
            if (entry.Source.Contains("gnum11", StringComparison.Ordinal))
            {
                timed.Add(new TimeSensitiveEntry(entry.Source, value, entry.Conversation, key));
            }

            if (entry.Conversation is not null)
            {
                builtScoped[ScopedKey(entry.Conversation, key)] = value;
            }

            if (!built.TryAdd(key, value))
            {
                // Expected, and no longer a loss: two entries whose English matches. The scoped index
                // keeps both reachable when they belong to different conversations; this one keeps the
                // first as the fallback for when the conversation cannot be determined.
                duplicates++;
                log.Debug("Duplicate text key (gameKey={GameKey}); the text-only index keeps the first. Key: {Key}",
                    entry.GameKey ?? "?", key);
            }
        }

        foreach (var (english, spanish) in model.NpcNames ?? [])
        {
            names[english] = spanish;
        }

        this.TargetLanguage ??= model.TargetLanguage;
        this.GameVersion ??= model.GameVersion;
        this.TranslationVersion ??= model.TranslationVersion;

        parseWatch.Stop();

        // Drop the parse graph before sampling the heap: without this we would be measuring the
        // transient List<FileEntry> as well as the dictionary we actually keep.
        model = null!;

        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
        var fileSize = new FileInfo(path).Length;

        log.Information(
            "  {Path}: {Entries} entries, {Scoped} scoped (target={Lang}, gameVersion={GameVersion}, "
            + "translationVersion={TranslationVersion})",
            Path.GetFileName(path),
            built.Count,
            builtScoped.Count,
            this.TargetLanguage ?? "?",
            this.GameVersion ?? "?",
            this.TranslationVersion ?? "not stated");

        log.Information(
            "  cost: file {FileMb:N1} MB | read {ReadMs} ms | parse+index {ParseMs} ms | "
            + "heap retained {HeapMb:N1} MB | total allocated {AllocMb:N1} MB",
            fileSize / 1024d / 1024d,
            readWatch.ElapsedMilliseconds,
            parseWatch.ElapsedMilliseconds,
            (heapAfter - heapBefore) / 1024d / 1024d,
            (allocatedAfter - allocatedBefore) / 1024d / 1024d);

        if (skippedEmpty > 0)
        {
            log.Warning("Skipped {Count} entries with a missing en or es field.", skippedEmpty);
        }

        if (duplicates > 0)
        {
            log.Warning("{Count} entries collided on an identical normalized key.", duplicates);
        }

        if (strippedEmphasis > 0)
        {
            log.Information(
                "Stripped literal asterisks from {Count} translation(s); the game renders them verbatim, not as italics.",
                strippedEmphasis);
        }

        if (evaluated > 0)
        {
            // timed, not this.timeSensitive. The field is assigned by the caller once every file has
            // been read, so reading it here reported the previous load's total — zero on the first
            // load of a session, which read as "no line depends on the clock" while two of them did.
            log.Information(
                "Resolved {Count} macro(s) through the game evaluator to build keys; {Timed} of them "
                + "depend on the Eorzean clock and will be rekeyed as it advances.",
                evaluated,
                timed.Count);
        }

        if (evaluationFailures > 0)
        {
            log.Warning(
                "{Count} source line(s) failed to evaluate and were dropped. Those lines stay in the game's "
                + "own language; nothing is injected over them.",
                evaluationFailures);
        }
    }

    /// <summary>
    ///     Resolves a macro string with the game's live state.
    /// </summary>
    /// <remarks>
    ///     Note this returning <c>true</c> does not mean the result is correct — only that nothing
    ///     threw. The evaluator treats unresolvable event-local parameters as zero and silently picks
    ///     a branch, so <c>&lt;if([lnum1&lt;lnum2],A,B)&gt;</c> yields B rather than failing. Entries
    ///     like that are excluded upstream by the extractor's <c>unresolved</c> flag; there is no way
    ///     to catch them here.
    /// </remarks>
    private bool TryEvaluate(string macro, out string resolved)
    {
        try
        {
            resolved = evaluator.EvaluateMacroString(macro).ExtractText();
            return !string.IsNullOrWhiteSpace(resolved);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Failed to evaluate macro: {Macro}", macro);
            resolved = string.Empty;
            return false;
        }
    }

    /// <summary>An entry whose resolved key depends on the Eorzean clock.</summary>
    private sealed class TimeSensitiveEntry(string source, string value, string? conversation, string currentKey)
    {
        /// <summary>The unevaluated source, kept so the key can be rebuilt against a later hour.</summary>
        public string Source { get; } = source;

        public string Value { get; } = value;

        /// <summary>Null for ambient lines, which live only in the text-only index.</summary>
        public string? Conversation { get; } = conversation;

        public string CurrentKey { get; set; } = currentKey;
    }

    private sealed class FileModel
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }

        [JsonPropertyName("sourceLanguage")] public string? SourceLanguage { get; set; }

        [JsonPropertyName("targetLanguage")] public string? TargetLanguage { get; set; }

        [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }

        [JsonPropertyName("translationVersion")] public string? TranslationVersion { get; set; }

        [JsonPropertyName("npcNames")] public Dictionary<string, string>? NpcNames { get; set; }

        [JsonPropertyName("entries")] public List<FileEntry>? Entries { get; set; }
    }

    private sealed class FileEntry
    {
        /// <summary>Provenance only — never used for lookup. See <see cref="TextKey" />.</summary>
        [JsonPropertyName("gameKey")]
        public string? GameKey { get; set; }

        /// <summary>
        ///     The conversation this line belongs to, e.g. <c>quest/047/SubWil901_04779</c>.
        /// </summary>
        /// <remarks>
        ///     Unlike <see cref="GameKey" /> this <em>is</em> a lookup key: the live quest handler
        ///     reports the same path at runtime, so it can be matched. Absent for ambient lines and for
        ///     runtime files written before the field existed, both of which fall back to text.
        /// </remarks>
        [JsonPropertyName("conversation")]
        public string? Conversation { get; set; }

        /// <summary>
        ///     The source line in its complete form: macro syntax intact wherever the line has any.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Not a flattened rendering. Macros must arrive unresolved so the plugin can put them
        ///         through the game's own evaluator and reproduce what the player is actually shown;
        ///         text with the macros deleted reads <c>"Off you go now, ."</c> against the game's
        ///         <c>"Off you go now, Mini."</c> and matches nothing.
        ///     </para>
        ///     <para>
        ///         Which language it is in is stated once, in the file header.
        ///     </para>
        /// </remarks>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        /// <summary>The translation. Which language it is in is stated once, in the header.</summary>
        [JsonPropertyName("target")]
        public string? Target { get; set; }
    }
}
