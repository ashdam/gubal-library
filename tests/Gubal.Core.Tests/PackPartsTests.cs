namespace GubalLibrary.Tests;

[Collection("Localization")]
public sealed class PackPartsTests
{
    [Fact]
    public void CategoryTreeAssignsEachSheetToOneNonEmptyPart()
    {
        var parts = PackParts.Groups.SelectMany(group => group.Parts).ToArray();
        var sheets = parts.SelectMany(part => part.Sheets).ToArray();

        Assert.NotEmpty(parts);
        Assert.All(parts, part => Assert.NotEmpty(part.Sheets));
        Assert.Equal(sheets.Length, sheets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(sheets, sheet => Assert.True(PackParts.IsKnown(sheet)));
        Assert.False(PackParts.IsKnown("__unassigned_test_sheet__"));
    }

    [Theory]
    [InlineData("exd/Quest_0_en.exd", "quest")]
    [InlineData("exd/quest/001/Test_0_en.exd", "quest/")]
    [InlineData("exd/cut_scene/001/Test_0_en.exd", "cut_scene/")]
    [InlineData("EXD/Custom/001/Test_0_en.exd", "custom/")]
    public void SheetSelectorDistinguishesFlatSheetsAndFamilies(string path, string expected)
    {
        Assert.Equal(expected, PackParts.SheetOf(path));
    }

    [Fact]
    public void CacheInvalidationPreservesSheetAssignments()
    {
        var sheets = PackParts.Groups.SelectMany(group => group.Parts).SelectMany(part => part.Sheets).ToArray();

        PackParts.Invalidate();

        Assert.Equal(sheets, PackParts.Groups.SelectMany(group => group.Parts).SelectMany(part => part.Sheets));
    }
}
