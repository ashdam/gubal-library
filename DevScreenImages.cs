namespace GubalLibrary;

internal sealed class DevScreenImages
{
    internal static readonly (uint Texture, uint Original, uint Translated, string Name)[] Banners =
    [
        (120001, 990101, 990102, "Quest Accepted"),
        (120002, 990103, 990104, "Quest Complete"),
        (120021, 990105, 990106, "Duty Commenced"),
        (120022, 990107, 990108, "Duty Complete"),
        (120023, 990109, 990110, "Duty Failed"),
        (120031, 990111, 990112, "Levequest Accepted"),
        (120032, 990113, 990114, "Levequest Complete"),
        (120024, 990115, 990116, "Forward!"),
        (120025, 990117, 990118, "Act Complete!"),
        (120026, 990119, 990120, "Next Act!"),
        (120055, 990121, 990122, "Delivery Complete"),
        (120068, 990123, 990124, "Allegiance Changed"),
        (120069, 990125, 990126, "Land Acquired!"),
        (120070, 990127, 990128, "Estate Hall Complete!"),
        (120071, 990129, 990130, "Private Chambers Acquired!"),
        (120072, 990131, 990132, "Apartment Acquired!"),
        (120073, 990133, 990134, "Relocation Complete!"),
        (120081, 990135, 990136, "FATE Joined"),
        (120082, 990137, 990138, "FATE Complete"),
        (120083, 990139, 990140, "FATE Failed"),
        (120084, 990141, 990142, "FATE Joined / Bonus"),
        (120085, 990143, 990144, "FATE Complete / Bonus"),
        (120086, 990145, 990146, "FATE Failed / Bonus"),
        (120092, 990147, 990148, "Reputation Up!"),
        (120093, 990149, 990150, "Treasure Obtained!"),
        (120094, 990151, 990152, "Treasure Found!"),
        (120095, 990153, 990154, "Venture Commenced!"),
        (120096, 990155, 990156, "Venture Accomplished!"),
        (120098, 990157, 990158, "All Vistas Recorded!"),
        (120101, 990159, 990160, "Fight!"),
        (120102, 990161, 990162, "Claws Win!"),
        (120103, 990163, 990164, "Fangs Win!"),
        (120104, 990165, 990166, "Draw!"),
        (120105, 990167, 990168, "PvP Rank Up!"),
        (120106, 990169, 990170, "Victory!"),
        (120107, 990171, 990172, "Defeat!"),
        (120108, 990173, 990174, "Rank Up!"),
        (120109, 990175, 990176, "Level Up!"),
        (120119, 990177, 990178, "Level Down..."),
        (120121, 990179, 990180, "Engage!"),
        (120122, 990181, 990182, "The Maelstrom Wins!"),
        (120123, 990183, 990184, "The Order of the Twin Adder Wins!"),
        (120124, 990185, 990186, "The Immortal Flames Win!"),
        (120126, 990187, 990188, "Sudden Death"),
        (120127, 990189, 990190, "Culling Time"),
        (120130, 990191, 990192, "Company Workshop Acquired"),
        (120131, 990193, 990194, "Materials Contributed"),
        (120132, 990195, 990196, "Progress Made"),
        (120133, 990197, 990198, "Excellent Progress Made!"),
        (120134, 990199, 990200, "Outstanding Progress Made!"),
        (120135, 990201, 990202, "Construction Complete!"),
        (120136, 990203, 990204, "Construction Complete! / Excellent Work!"),
        (120137, 990205, 990206, "Construction Complete! / Outstanding Work!"),
        (120140, 990207, 990208, "Airship Registered!"),
        (120142, 990209, 990210, "Voyage Complete"),
        (120143, 990211, 990212, "Submersible Registered!"),
        (120150, 990213, 990214, "Briefing"),
        (120152, 990215, 990216, "You Win!"),
        (120153, 990217, 990218, "You Lose!"),
        (120154, 990219, 990220, "Draw"),
        (120155, 990221, 990222, "You Sound the Retreat..."),
        (120156, 990223, 990224, "The Enemy Retreats!"),
        (120161, 990225, 990226, "Current Rating"),
        (120162, 990227, 990228, "Promotion Qualifier Available!"),
        (120172, 990229, 990230, "Mission Start!"),
        (120173, 990231, 990232, "Mission Success!"),
        (120174, 990233, 990234, "Row Complete!"),
        (120175, 990235, 990236, "Satisfaction Up!"),
        (120177, 990237, 990238, "Start"),
        (120178, 990239, 990240, "Complete"),
        (120179, 990241, 990242, "Failed"),
        (120180, 990243, 990244, "Time’s Up!"),
        (120903, 990245, 990246, "Trials of the Braves Complete / Saga of the Zodiac Weapons"),
        (120906, 990247, 990248, "Soul Attunement Complete / Saga of the Zodiac Weapons"),
        (120913, 990249, 990250, "Aetherial Condensation Complete! / Saga of the Anima Weapons"),
        (120920, 990251, 990252, "Trials of the Moon"),
        (120921, 990253, 990254, "Complete!"),
        (121001, 990255, 990256, "Quest Accepted / Seasonal Event"),
        (121002, 990257, 990258, "Quest Complete / Seasonal Event"),
        (121006, 990259, 990260, "Elemental Conflict Joined"),
        (121007, 990261, 990262, "Elemental Conflict Complete!"),
        (121008, 990263, 990264, "Elemental Conflict Failed..."),
        (121011, 990265, 990266, "FATE Joined / Seasonal Event"),
        (121012, 990267, 990268, "FATE Complete / Seasonal Event"),
        (121013, 990269, 990270, "FATE Failed / Seasonal Event"),
        (121016, 990271, 990272, "Notorious Monster Battle Joined"),
        (121017, 990273, 990274, "Notorious Monster Battle Complete!"),
        (121018, 990275, 990276, "Notorious Monster Battle Failed..."),
        (121081, 990277, 990278, "Society Quest Accepted"),
        (121082, 990279, 990280, "Society Quest Complete"),
        (121101, 990281, 990282, "The Ties That Bind / Special Quest Accepted"),
        (121102, 990283, 990284, "Congratulations! / Special Quest Complete"),
        (121111, 990285, 990286, "Skirmish Joined"),
        (121112, 990287, 990288, "Skirmish Won!"),
        (121113, 990289, 990290, "Skirmish Lost..."),
        (121116, 990291, 990292, "Critical Engagement Won!"),
        (121117, 990293, 990294, "Critical Engagement Lost..."),
        (121120, 990295, 990296, "Solo Engagement Won!"),
        (121121, 990297, 990298, "Solo Engagement Lost..."),
        (121246, 990299, 990300, "Fête Joined / Firmament Special Event"),
        (121248, 990301, 990302, "Fête Complete / Firmament Special Event"),
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
        foreach (var banner in Banners)
        {
            this.Add(Path.Combine(directory, "banners", "original"), banner.Texture.ToString(), banner.Original);
            this.Add(Path.Combine(directory, "banners", "translated"), banner.Texture.ToString(), banner.Translated);
        }
    }

    private void Add(string directory, string name, uint id)
    {
        var normal = Path.Combine(directory, $"{name}.tex");
        var high = Path.Combine(directory, $"{name}_hr1.tex");
        if (!Valid(normal, 1280, 360) || !Valid(high, 2560, 720))
        {
            return;
        }

        this.Files.Add(new PackPage($"ui/icon/990000/{id}.tex", normal, "addon"));
        this.Files.Add(new PackPage($"ui/icon/990000/{id}_hr1.tex", high, "addon"));
        this.Available.Add(id);
    }

    private static bool Valid(string path, ushort width, ushort height)
    {
        if (!File.Exists(path) || path.Length > ExdRedirector.MaxLocalPathLength)
        {
            return false;
        }

        using var file = File.OpenRead(path);
        if (file.Length < 80) return false;
        using var reader = new BinaryReader(file);
        file.Position = 4;
        var format = reader.ReadUInt32();
        // The original HD texture uses BC3. Generated textures use BGRA.
        var length = format switch
        {
            0x1450 => 80L + width * (long)height * 4,
            0x3431 => 80L + ((width + 3) / 4) * (long)((height + 3) / 4) * 16,
            _ => -1,
        };
        return file.Length == length
            && reader.ReadUInt16() == width && reader.ReadUInt16() == height
            && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1;
    }
}
