namespace GubalLibrary;

internal sealed class DevScreenImages
{
    internal static readonly (uint Id, uint SourceId, string Name)[] Images =
    [
        (120001, 120001, "Quest Accepted"),
        (120011, 120001, "Quest Accepted"),
        (120002, 120002, "Quest Complete"),
        (120012, 120002, "Quest Complete"),
        (120021, 120021, "Duty Commenced"),
        (120033, 120021, "Duty Commenced"),
        (120041, 120021, "Duty Commenced"),
        (120022, 120022, "Duty Complete"),
        (120034, 120022, "Duty Complete"),
        (120042, 120022, "Duty Complete"),
        (120023, 120023, "Duty Failed"),
        (120035, 120023, "Duty Failed"),
        (120043, 120023, "Duty Failed"),
        (120031, 120031, "Levequest Accepted"),
        (120032, 120032, "Levequest Complete"),
    ];


    internal static readonly (uint Id, uint SourceId, string Name)[] Cinematics =
    [
        (120502, 120502, "And thus did dawn break"),
        (120503, 120503, "But where there is light"),
        (120518, 120518, "To preserve the dawn"),
        (120519, 120519, "Their hearts filled with hope"),
        (120520, 120520, "Spires of deception"),
        (120521, 120521, "Beyond simmering shadow"),
        (120522, 120522, "At the far edge of fate"),
        (120523, 120523, "A dawn of liberation"),
        (120525, 120525, "So fell the hunter"),
        (120526, 120526, "A requiem for heroes"),
        (120527, 120527, "In fields of tranquil light"),
        (120529, 120529, "Though blazing skies"),
        (120530, 120530, "At solemn dawn"),
        (120531, 120531, "Our final curtain"),
        (120532, 120532, "Weigh anchor now"),
        (120533, 120533, "Break the horizon"),
        (120535, 120535, "As skies aflame"),
        (120536, 120536, "Farewell to Dawn"),
        (120537, 120537, "Winter's wail"),
    ];

    internal List<PackPage> Files { get; } = [];
    internal HashSet<uint> Available { get; } = [];

    internal DevScreenImages(string directory, string? language)
    {
        if (string.IsNullOrEmpty(language) || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            return;
        }

        directory = Path.Combine(directory, language.ToLowerInvariant());
        foreach (var (id, sourceId, _) in Images.Concat(Cinematics))
        {
            var normal = Path.Combine(directory, $"{sourceId}.tex");
            var high = Path.Combine(directory, $"{sourceId}_hr1.tex");
            var height = Height(id);
            if (!Valid(normal, 1280, height) || !Valid(high, 2560, (ushort)(height * 2)))
            {
                continue;
            }

            var previewId = PreviewId(id);
            this.Files.Add(new PackPage($"ui/icon/990000/{previewId}.tex", normal, "addon"));
            this.Files.Add(new PackPage($"ui/icon/990000/{previewId}_hr1.tex", high, "addon"));
            this.Available.Add(previewId);
        }
    }

    internal static uint PreviewId(uint id) => 990000 + id - 120000;

    internal static ushort Height(uint id) => Cinematics.Any(image => image.Id == id) ? (ushort)256 : (ushort)360;

    private static bool Valid(string path, ushort width, ushort height)
    {
        if (!File.Exists(path) || path.Length > ExdRedirector.MaxLocalPathLength)
        {
            return false;
        }

        using var file = File.OpenRead(path);
        if (file.Length != 80L + width * height * 4L)
        {
            return false;
        }

        using var reader = new BinaryReader(file);
        file.Position = 4;
        return reader.ReadUInt32() == 0x1450
            && reader.ReadUInt16() == width && reader.ReadUInt16() == height
            && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1;
    }
}
