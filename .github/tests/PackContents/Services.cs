namespace CheapLoc
{
    public static class Loc
    {
        public static string Localize(string key, string fallback) => fallback;
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
