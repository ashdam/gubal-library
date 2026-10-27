using System.Text.Json;
using Loc = GubalLibrary.CoreLanguage;

namespace GubalLibrary;

internal sealed record TranslationPart(
    string Id, string Name, string Description, string[] Sheets);

internal sealed record PartGroup(
    string Id, string Name, string Description, TranslationPart[] Parts);

internal static class PackParts
{
    public static string UncategorizedGroupName =>
        Loc.Localize("Group.Other.Name", "Uncategorized");

    public static PartGroup[] Groups => groups ??= Build();

    private static PartGroup[]? groups;
    private static HashSet<string>? known;

    public static void Invalidate()
    {
        groups = null;
        known = null;
    }

    public static bool IsKnown(string sheet) =>
        (known ??= new HashSet<string>(Groups.SelectMany(g => g.Parts).SelectMany(p => p.Sheets),
            StringComparer.OrdinalIgnoreCase)).Contains(sheet);

    private static PartGroup[] Build()
    {
        using var stream = typeof(PackParts).Assembly.GetManifestResourceStream("GubalLibrary.sheet-category-tree.json")
            ?? throw new InvalidDataException("The localization tree resource is missing.");
        var tree = JsonSerializer.Deserialize<Tree>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The localization tree is empty.");
        if (tree.SchemaVersion != 1 || tree.Children.Length == 0)
            throw new InvalidDataException("The localization tree schema is not supported.");

        var sheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        return tree.Children.Select(group =>
        {
            ValidateNode(group);
            if (!groupIds.Add(group.Id) || group.Sheets.Length != 0 || group.Children.Length == 0)
                throw new InvalidDataException($"Invalid localization group: {group.Id}");
            var partIds = new HashSet<string>(StringComparer.Ordinal);
            var parts = group.Children.Select(part =>
            {
                if (!partIds.Add(part.Id))
                    throw new InvalidDataException($"Duplicate localization part: {group.Id}/{part.Id}");
                var selected = SheetsOf(part).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (selected.Length == 0 || selected.Any(sheet => !sheets.Add(sheet)))
                    throw new InvalidDataException($"Empty or overlapping localization part: {group.Id}/{part.Id}");
                var path = $"{group.Id}/{part.Id}";
                return new TranslationPart(part.Id, Text(path, "Name", part.Name),
                    Text(path, "Description", part.Description), selected);
            }).ToArray();
            return new PartGroup(group.Id, Text(group.Id, "Name", group.Name),
                Text(group.Id, "Description", group.Description), parts);
        }).ToArray();
    }

    // Inventory levels share the sheet switch of their parent part.
    private static IEnumerable<string> SheetsOf(Node node)
    {
        ValidateNode(node);
        foreach (var sheet in node.Sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet) || sheet != sheet.Trim() || sheet.Contains('\\') || sheet.Contains('#'))
                throw new InvalidDataException($"Invalid sheet selector: {sheet}");
            yield return sheet.ToLowerInvariant();
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in node.Children)
        {
            if (!ids.Add(child.Id))
                throw new InvalidDataException($"Duplicate inventory node: {child.Id}");
            foreach (var sheet in SheetsOf(child))
                yield return sheet;
        }
    }

    private static void ValidateNode(Node node)
    {
        if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Name))
            throw new InvalidDataException("Localization nodes require an ID and a name.");
    }

    private static string Text(string path, string field, string value) => string.IsNullOrEmpty(value)
        ? string.Empty : Loc.Localize($"Tree.{path.Replace('/', '.')}.{field}", value);

    private sealed class Tree
    {
        public int SchemaVersion { get; init; }
        public Node[] Children { get; init; } = [];
    }

    private sealed class Node
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string[] Sheets { get; init; } = [];
        public Node[] Children { get; init; } = [];
    }

    // Nested families retain the slash to distinguish Quest titles from quest/ dialogue.
    public static string SheetOf(string gamePath)
    {
        var rel = gamePath.StartsWith("exd/", StringComparison.OrdinalIgnoreCase) ? gamePath[4..] : gamePath;

        var slash = rel.IndexOf('/');
        if (slash >= 0)
        {
            return rel[..(slash + 1)].ToLowerInvariant();
        }

        var name = Path.GetFileNameWithoutExtension(rel);
        var tokens = name.Split('_');
        var end = tokens.Length;

        // Remove the language suffix before the page row number.
        if (end > 1 && tokens[end - 1].Length <= 4 && tokens[end - 1].All(char.IsAsciiLetter))
        {
            end--;
        }

        // Remove the page row number.
        if (end > 1 && tokens[end - 1].All(char.IsAsciiDigit))
        {
            end--;
        }

        return string.Join('_', tokens[..end]).ToLowerInvariant();
    }
}
