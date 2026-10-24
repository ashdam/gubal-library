using Dalamud.Configuration;

namespace GubalLibrary;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public Guid UsageInstallationId { get; set; }
    public long UsageSequence { get; set; }

    public string PackSource { get; set; } = string.Empty;

    public string LanguagePackPath { get; set; } = string.Empty;

    public string InstalledFrom { get; set; } = string.Empty;

    public string OwnPackFolder { get; set; } = string.Empty;

    // Apply the same page selection to the game and Dalamud.
    public bool ServeLanguagePack { get; set; }

    public bool AutoUpdatePack { get; set; } = true;

    public bool Debug { get; set; }

    public bool EnableCodexLinks { get; set; }

    public bool LifestreamCompatibility { get; set; }

    // Store lowercase sheet keys, including keys absent from the current pack.
    public HashSet<string> DisabledSheets { get; set; } = [];
}
