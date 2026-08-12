using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <param name="GamePath">What the game asks for, relative to the pack root, with its own separators.</param>
/// <param name="LocalPath">The file on disk that answers it.</param>
/// <param name="Sheet">Which part of the translation it belongs to. See <see cref="PackParts.SheetOf" />.</param>
internal readonly record struct PackPage(string GamePath, string LocalPath, string Sheet);

/// <param name="Sheets">Only the sheets of this part that the installed pack actually holds.</param>
internal sealed record PartView(TranslationPart Part, string[] Sheets);

/// <param name="Description">Where this lot is seen in the game.</param>
/// <param name="Warning">The group's own caveat, or null when it has none.</param>
/// <param name="Image">Before-and-after picture for this group, or null when none was taken.</param>
internal sealed record GroupView(
    string Name, string Description, string? Warning, PartView[] Parts, string? Image);

/// <summary>
///     What an installed language pack holds, read once and kept.
/// </summary>
/// <remarks>
///     <para>
///         Split out of <see cref="ExdRedirector" /> because the settings window needs the same answer
///         and cannot get it from there: the redirector only exists when a pack is being served, and
///         the window has to show what is in a pack that is switched off, or installed and waiting for
///         a restart — which are exactly the moments somebody is deciding what to switch on.
///     </para>
///     <para>
///         <b>Read once.</b> A pack is four thousand files; ImGui draws sixty times a second. This is
///         loaded when the pack path changes and cached, never touched from a draw call.
///     </para>
/// </remarks>
internal sealed class PackContents
{
    private readonly List<PackPage> pages;

    private PackContents(List<PackPage> pages, int tooLong)
    {
        this.pages = pages;
        this.TooLong = tooLong;
        this.Layout = BuildLayout(pages);
    }

    /// <summary>Pages refused for sitting at too long a path, which are served by nobody.</summary>
    public int TooLong { get; }

    /// <summary>Every page in the pack, whether or not its part is switched on.</summary>
    public int PageCount => this.pages.Count;

    /// <summary>The groups and parts this pack actually holds, in the order they are drawn.</summary>
    /// <remarks>
    ///     A group whose sheets are all absent is left out rather than drawn empty: a pack that
    ///     translates no interface should not offer a switch for one.
    /// </remarks>
    public IReadOnlyList<GroupView> Layout { get; }

    /// <summary>How many checkboxes this pack offers, across every group.</summary>
    public int PartCount => this.Layout.Sum(g => g.Parts.Length);

    /// <summary>
    ///     The parts that are switched off, by the name they are switched off under.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Any sheet of it being off makes the part off</b>, not all of them. Ticking a box
    ///         writes every sheet it covers at once, so the half-and-half state cannot be reached by
    ///         using the window — but it is reached the moment a later build of this plugin folds
    ///         another sheet into an existing part, which is exactly when somebody's saved choice
    ///         covers some of a part and not the rest.
    ///     </para>
    ///     <para>
    ///         Of the two ways to round that off, this is the one that cannot mislead. Calling a
    ///         half-served part "on" puts a tick beside text the user is looking at in English.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> PartsOff(ICollection<string> disabledSheets)
    {
        if (disabledSheets.Count == 0)
        {
            return [];
        }

        return this.Layout
            .SelectMany(g => g.Parts)
            .Where(p => p.Sheets.Any(disabledSheets.Contains))
            .Select(p => p.Part.Name)
            .ToArray();
    }

    /// <summary>
    ///     Reads a pack folder. A folder that is missing or empty yields empty contents rather than
    ///     an error — whoever asked is the one who reports that, and both callers already do.
    /// </summary>
    public static PackContents Load(string directory, int maxLocalPathLength)
    {
        var pages = new List<PackPage>();
        var tooLong = 0;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new PackContents(pages, tooLong);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.exd", SearchOption.AllDirectories))
        {
            if (file.Length > maxLocalPathLength)
            {
                tooLong++;
                continue;
            }

            // The folder mirrors the archive, so the game path is the relative path with the
            // separators the game uses.
            var gamePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            pages.Add(new PackPage(gamePath, file, PackParts.SheetOf(gamePath)));
        }

        return new PackContents(pages, tooLong);
    }

    /// <summary>
    ///     The pages to hand the game, with the switched-off parts left out.
    /// </summary>
    /// <remarks>
    ///     The whole feature is this method. A page that is not in the returned map is not in the
    ///     redirector's dictionary, so its read misses and the game reads its own copy — no fallback
    ///     text, no second code path, and nothing extra on the hot read path.
    /// </remarks>
    public Dictionary<string, string> Servable(ICollection<string> disabledSheets)
    {
        var served = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in this.pages)
        {
            if (disabledSheets.Count == 0 || !disabledSheets.Contains(page.Sheet))
            {
                served[page.GamePath] = page.LocalPath;
            }
        }

        return served;
    }

    /// <summary>Says in the log which parts were held back, since the page count alone cannot.</summary>
    /// <remarks>
    ///     A pack serving fewer pages than it holds looks identical to a pack that failed to read
    ///     half of itself. One line naming the sheets is the difference between a decision and a bug.
    /// </remarks>
    public void LogOmissions(IPluginLog log, ICollection<string> disabledSheets, int served)
    {
        if (disabledSheets.Count == 0 || served == this.pages.Count)
        {
            return;
        }

        var off = this.pages
            .Select(p => p.Sheet)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(disabledSheets.Contains)
            .Order(StringComparer.OrdinalIgnoreCase);

        log.Information(
            "{Count} page(s) are not being served because you switched their part off: {Sheets}.",
            this.pages.Count - served,
            string.Join(", ", off));
    }

    /// <summary>
    ///     Matches the curated table against what this pack holds, keeping only what is present.
    /// </summary>
    /// <remarks>
    ///     Sheets the table does not name are gathered at the end, one checkbox each. Dropping them
    ///     would mean a pack in another language had text nobody could switch off and no sign that it
    ///     was there.
    /// </remarks>
    private static IReadOnlyList<GroupView> BuildLayout(List<PackPage> pages)
    {
        var present = new HashSet<string>(pages.Select(p => p.Sheet), StringComparer.OrdinalIgnoreCase);
        var groups = new List<GroupView>();

        foreach (var group in PackParts.Groups)
        {
            var parts = new List<PartView>();

            foreach (var part in group.Parts)
            {
                var held = part.Sheets.Where(present.Contains).ToArray();
                if (held.Length > 0)
                {
                    parts.Add(new PartView(part, held));
                }
            }

            if (parts.Count > 0)
            {
                groups.Add(new GroupView(
                    group.Name, group.Description, group.Warning, parts.ToArray(), group.Image));
            }
        }

        var unknown = present
            .Where(s => !PackParts.IsKnown(s))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(s => new PartView(
                new TranslationPart(s, "This build of the plugin has no name for this one.", [s]), [s]))
            .ToArray();

        if (unknown.Length > 0)
        {
            groups.Add(new GroupView(
                PackParts.OtherGroupName,
                "Text this pack translates that this build of the plugin has no name for, listed "
                + "under the game's own name for it. A pack in another language may well cover "
                + "things this one does not.",
                null,
                unknown,
                null));
        }

        return groups;
    }
}
