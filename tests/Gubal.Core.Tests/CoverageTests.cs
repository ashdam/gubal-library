using System.Text.Json;

namespace GubalLibrary.Tests;

public sealed class CoverageTests
{
    [Fact]
    public void ExpansionCoverageWeightsTextCountsAndOmitsExcludedRows()
    {
        var manifest = JsonSerializer.Deserialize<PackManifest>("""
            {"coverage":{"groups":[
              {"expansion":"Heavensward","translated":10,"total":10},
              {"expansion":"Heavensward","translated":0,"total":90},
              {"expansion":null,"translated":15,"total":30}
            ],"excludedFromLocalization":[
              {"expansion":"Heavensward","translated":0,"total":900}
            ]}}
            """);

        var expansions = Assert.IsType<PackCoverage>(Assert.IsType<PackManifest>(manifest).Coverage)
            .ByExpansion.ToDictionary(expansion => expansion.Name);

        Assert.Equal(10, expansions["Heavensward"].Percent);
        Assert.Equal(50, expansions[string.Empty].Percent);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(1, 2, 50)]
    [InlineData(10, 10, 100)]
    public void ExpansionPercentageHandlesEmptyAndCompletedGroups(int translated, int total, double expected)
    {
        Assert.Equal(expected, new ExpansionCoverage("test", translated, total).Percent);
    }

    [Fact]
    public void ExpansionPercentagePreservesFractions()
    {
        Assert.InRange(new ExpansionCoverage("test", 1, 3).Percent, 33.3, 33.4);
    }

    [Fact]
    public void CoverageWithoutGroupsHasNoExpansionBreakdown()
    {
        Assert.Empty(new PackCoverage().ByExpansion);
    }
}
