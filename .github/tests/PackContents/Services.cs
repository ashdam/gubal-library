namespace CheapLoc
{
    public static class Loc
    {
        public static Dictionary<string, string> Messages { get; set; } = [];

        public static string Localize(string key, string fallback) => Messages.GetValueOrDefault(key, fallback);

        public static string Localize(string key, string fallback, System.Reflection.Assembly assembly) => Localize(key, fallback);

        public static void SetupWithFallbacks(System.Reflection.Assembly assembly) { }

        public static void Setup(string translations, System.Reflection.Assembly assembly) =>
            Messages = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(translations)!;
    }
}

namespace Dalamud.Configuration
{
    public interface IPluginConfiguration
    {
        int Version { get; set; }
    }
}

namespace Dalamud.Plugin.Services
{
    public interface IPluginLog
    {
        void Information(string template, params object[] values);
        void Debug(string template, params object[] values);
    }
}
