using System.Text.Json;

namespace GubalLibrary.Tests;

[Collection("Localization")]
public sealed class PackVersionTests : IDisposable
{
    private const string Older = "2026.09.01.0000.0000";
    private const string Current = "2026.09.15.0000.0000";

    [Fact]
    public void EqualGameVersionsHaveNoError()
    {
        Assert.Null(PackVersion.Error(Current, Current));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("it")]
    public void VersionMismatchUsesTheSelectedLanguageAndBothVersions(string language)
    {
        var messages = this.LoadLanguage(language);
        var template = messages["Pack.GameVersionMismatch"];

        Assert.Equal(string.Format(template, Older, Current), PackVersion.Error(Older, Current));
        Assert.Equal(string.Format(template, Current, Older), PackVersion.Error(Current, Older));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("it")]
    public void MissingGameOrPackVersionUsesTheLocalizedError(string language)
    {
        var messages = this.LoadLanguage(language);
        var expected = messages["Pack.GameVersionUnknown"];

        foreach (var missing in new string?[] { null, "", " " })
        {
            Assert.Equal(expected, PackVersion.Error(missing, Current));
            Assert.Equal(expected, PackVersion.Error(Current, missing));
            Assert.Equal(expected, PackVersion.Error(missing, missing));
        }
    }

    [Fact]
    public void ManifestKeepsTheFullGameVersion()
    {
        var manifest = JsonSerializer.Deserialize<PackManifest>("""{"gameVersion":"2026.09.01.0000.0000"}""");

        Assert.Equal(Older, Assert.IsType<PackManifest>(manifest).GameVersion);
    }

    private Dictionary<string, string> LoadLanguage(string language)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "loc", language + ".json")));
        var messages = json.RootElement.EnumerateObject().ToDictionary(
            property => property.Name, property => property.Value.GetProperty("message").GetString()!);
        CoreLanguage.Apply(json.RootElement.GetRawText());
        return messages;
    }

    public void Dispose() => CoreLanguage.Apply(null);
}
