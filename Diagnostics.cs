using Dalamud.Plugin.Services;

namespace GubalLibrary;

/// <summary>
///     The trace lines of the plugin, quiet unless <c>/gubal debug on</c>.
/// </summary>
/// <remarks>
///     By default the plugin writes only warnings and errors to the log. With debug on, the same
///     lines go out at Information level, and the SqPack probe attaches at the next load. With debug
///     off, they go out at Debug level, which Dalamud does not write at its default log level.
/// </remarks>
internal static class Diagnostics
{
    /// <summary>Set once at load from the configuration. A change applies at the next load.</summary>
    public static bool Enabled { get; set; }

    public static void Log(IPluginLog log, string template, params object[] values)
    {
        if (Enabled)
        {
            log.Information(template, values);
        }
        else
        {
            log.Debug(template, values);
        }
    }
}
