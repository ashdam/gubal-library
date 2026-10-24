using Dalamud.Plugin.Services;

namespace GubalLibrary;

internal static class DalamudPackInstaller
{
    public static PackInstaller Create(IPluginLog log, string configDirectory) =>
        new(new DalamudInstallLog(log), configDirectory);

    public static InstallResult? ActivatePending(this PackInstaller installer, Configuration config, Action<Configuration> save) =>
        installer.ActivatePending((path, source) =>
        {
            var previous = (config.LanguagePackPath, config.InstalledFrom, config.PackSource);
            try
            {
                config.LanguagePackPath = path;
                config.InstalledFrom = source;
                config.PackSource = source;
                save(config);
            }
            catch
            {
                (config.LanguagePackPath, config.InstalledFrom, config.PackSource) = previous;
                throw;
            }
        });

    private sealed class DalamudInstallLog(IPluginLog log) : IPackInstallLog
    {
        public void Information(string message, params object[] values) => Diagnostics.Log(log, message, values);
        public void Warning(string message, params object[] values) => log.Warning(message, values);
        public void Warning(Exception error, string message, params object[] values) => log.Warning(error, message, values);
        public void Error(Exception error, string message, params object[] values) => log.Error(error, message, values);
    }
}
