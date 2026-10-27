using System.Diagnostics;

namespace GubalLibrary;

internal sealed record LauncherAccount(bool Steam = false, bool FreeTrial = false)
{
    public ProcessStartInfo StartInfo(string launcher)
    {
        var start = new ProcessStartInfo(launcher)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(launcher),
        };
        var argument = Argument();
        if (argument.Length != 0) start.ArgumentList.Add(argument);
        if (Steam)
        {
            var appId = FreeTrial ? "312060" : "39210";
            start.Environment["SteamAppId"] = appId;
            start.Environment["SteamGameId"] = appId;
        }
        return start;
    }

    private string Argument() => (Steam, FreeTrial) switch
    {
        (true, true) => "-issteamfreetrial",
        (true, false) => "-issteam",
        (false, true) => "-mode=freetrial",
        _ => "",
    };
}
