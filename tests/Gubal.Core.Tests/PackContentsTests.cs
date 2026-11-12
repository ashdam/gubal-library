namespace GubalLibrary.Tests;

[Collection("Localization")]
public sealed class PackContentsTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    [Fact]
    public void MissingPackHasNoResources()
    {
        var contents = PackContents.Load(Path.Combine(this.directory.Path, "missing"), int.MaxValue);

        Assert.Equal(0, contents.PageCount);
        Assert.Empty(contents.Servable([]));
        Assert.Empty(contents.ServableLayouts([]));
        Assert.Empty(contents.ServableScreenImages([]));
    }

    [Fact]
    public void LayoutsDoNotCreateTextPagesOrTranslationSwitches()
    {
        this.directory.Write("ui/uld/test.uld");

        var contents = this.Load();

        Assert.Equal(0, contents.PageCount);
        Assert.Equal(0, contents.PartCount);
        Assert.Single(contents.ServableLayouts([]));
    }

    [Fact]
    public void LayoutSelectionAcceptsNestedUldFilesAndIgnoresOtherFiles()
    {
        var first = this.directory.Write("ui/uld/Test.ULD");
        var nested = this.directory.Write("ui/uld/nested/Another.uld");
        foreach (var file in new[] { "ui/uld/ignored.tex", "ui/uld/ignored.uld.bak", "ui/ignored.uld", "exd/ignored.uld" })
            this.directory.Write(file);

        var layouts = this.Load().ServableLayouts([]);

        Assert.Equal(2, layouts.Count);
        Assert.Equal(first, layouts["UI/ULD/TEST.uld"]);
        Assert.Equal(nested, layouts["ui/uld/nested/Another.uld"]);
    }

    [Theory]
    [InlineData("addon", 0)]
    [InlineData("lobby", 1)]
    public void AddonSwitchControlsLayoutsAndScreenImages(string disabledSheet, int expectedCount)
    {
        this.directory.Write("ui/uld/test.uld");
        this.directory.Write("ui/icon/120000/en/120021.tex");

        var contents = this.Load();

        Assert.Equal(expectedCount, contents.ServableLayouts([disabledSheet]).Count);
        Assert.Equal(expectedCount, contents.ServableScreenImages([disabledSheet]).Count);
    }

    [Fact]
    public void DisabledSheetDoesNotDisableOtherPagesOrFonts()
    {
        this.directory.Write("exd/addon_0_en.exd");
        this.directory.Write("exd/lobby_0_en.exd");
        this.directory.Write("common/font/axis_12.fdt");

        var contents = this.Load();

        Assert.Equal(["exd/lobby_0_en.exd"], contents.Servable(["addon"]).Keys);
        Assert.Single(contents.Fonts);
    }

    [Fact]
    public void ScreenImageSelectionIncludesHighResolutionAndCinematicTextures()
    {
        string[] accepted =
        [
            "ui/icon/120000/en/120021.tex",
            "ui/icon/121000/en/121001.tex",
            "ui/icon/121000/en/121001_hr1.tex",
            "ui/uld/moviesubtitle_test.tex",
        ];
        foreach (var file in accepted.Concat(["ui/icon/120000/en/ignored.png", "ui/icon/000000/000001.tex"]))
            this.directory.Write(file);

        var images = this.Load().ServableScreenImages([]).Select(image => image.GamePath);

        Assert.Equal(accepted.Order(), images.Order());
    }

    [Theory]
    [InlineData("ui/icon/120000/en/120021.tex", true)]
    [InlineData("ui/icon/121000/en/121001_hr1.tex", true)]
    [InlineData("ui/uld/moviesubtitle_test.tex", true)]
    [InlineData("ui/icon/990000/990101.tex", false)]
    public void ScreenImagePathsDistinguishServedResources(string path, bool expected)
    {
        Assert.Equal(expected, PackContents.IsScreenImagePath(System.Text.Encoding.UTF8.GetBytes(path)));
    }

    [Fact]
    public void PathsBeyondTheNativeLimitAreRefused()
    {
        this.directory.Write("ui/uld/short.uld");
        var longPath = this.directory.Write("ui/uld/" + new string('x', 80) + ".uld");

        var contents = PackContents.Load(this.directory.Path, longPath.Length - 1);

        Assert.Equal(1, contents.TooLong);
        Assert.Single(contents.ServableLayouts([]));
    }

    private PackContents Load() => PackContents.Load(this.directory.Path, int.MaxValue);

    public void Dispose() => this.directory.Dispose();
}
