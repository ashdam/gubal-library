using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>Writes diagnostic traces only while debug is enabled.</summary>
internal static class Diagnostics
{
    public static bool Enabled { get; set; }

    public static void Log(IPluginLog log, string template, params object[] values)
    {
        if (Enabled)
        {
            log.Information(template, values);
        }
    }
}
