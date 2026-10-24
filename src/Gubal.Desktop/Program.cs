using Velopack;

namespace GubalDesktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // FFXIV can hold the runtime files open after Desktop exits.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (args is ["--check"])
        {
            var files = new[] { "Gubal.Core.dll", "Gubal.Game.dll", "GubalStandalone.Runtime.exe", "GubalStandalone.Runtime.dll",
                "GubalStandalone.Runtime.runtimeconfig.json", "GubalStandalone.Bootstrap.dll", "GubalStandalone.HostCheck.exe",
                "GubalStandalone.Launcher.exe", "GubalStandalone.Launcher.dll", "FFXIVClientStructs.dll", "Lumina.dll" };
            var missing = files.Where(file => !File.Exists(Path.Combine(AppContext.BaseDirectory, "engine", file))).ToArray();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Missing = missing, Installations = GameDetection.Find() }));
            return missing.Length == 0 ? 0 : 1;
        }
        using var single = new Mutex(true, @"Local\GubalDesktop", out var first);
        if (!first)
        {
            MessageBox.Show(UiText.Get("AlreadyOpen"), "Gubal Desktop");
            return 0;
        }
        ApplicationConfiguration.Initialize();
        using var fonts = new DesktopFonts();
        Application.SetDefaultFont(fonts.Default);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new MainWindow());
        return 0;
    }
}
