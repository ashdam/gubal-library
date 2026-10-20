using System.Diagnostics;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using GubalLibrary;
using Microsoft.Win32;

namespace GubalDesktop;

internal sealed record DesktopSettings(string GameFolder = "", string Language = "", bool AutoUpdate = true);

internal sealed class DesktopStore : IPackInstallLog
{
    public string Root { get; } = AppContext.GetData("Gubal.Desktop.DataDirectory") as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GubalDesktop");
    public DesktopStore() => Directory.CreateDirectory(Root);
    public DesktopSettings Read() => File.Exists(Path.Combine(Root, "settings.json"))
        ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new()
        : new();
    public void Save(DesktopSettings settings)
    {
        var path = Path.Combine(Root, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public string LanguageFolder(string language)
    {
        if (!KnownPacks.All.Any(pack => pack.Code == language)) throw new InvalidDataException("Unknown language.");
        var path = Path.Combine(Root, "packs", language);
        Directory.CreateDirectory(path);
        return path;
    }
    public void Information(string message, params object[] values) => Write(message, values);
    public void Warning(string message, params object[] values) => Write(message, values);
    public void Warning(Exception error, string message, params object[] values) => Write(message + " " + error.Message, values);
    public void Error(Exception error, string message, params object[] values) => Write(message + " " + error, values);
    private void Write(string message, object[] values)
    {
        try { File.AppendAllText(Path.Combine(Root, "desktop.log"), message + " " + string.Join(" ", values) + Environment.NewLine); }
        catch (IOException) { }
    }
}

internal static class GameDetection
{
    public static string? Normalize(string folder)
    {
        try
        {
            foreach (var path in new[] { folder, Path.GetDirectoryName(folder)!, Path.Combine(folder, "FINAL FANTASY XIV - A Realm Reborn") })
                if (File.Exists(Path.Combine(path, "game", "ffxiv_dx11.exe")) && File.Exists(Path.Combine(path, "boot", "ffxivboot64.exe")))
                    return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static string[] Find()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path) { if (path is { Length: > 0 } && Normalize(path) is { } valid) candidates.Add(valid); }
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var branch in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var root = hive.OpenSubKey(branch);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is string display && display.Contains("FINAL FANTASY XIV", StringComparison.OrdinalIgnoreCase))
                        Add(key.GetValue("InstallLocation") as string);
                }
            }
            catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string path) steamRoots.Add(path);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
        steamRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (var steam in steamRoots.ToArray())
        {
            var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraries))
                foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s*\"([^\"]+)\""))
                    steamRoots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        foreach (var steam in steamRoots) Add(Path.Combine(steam, "steamapps", "common", "FINAL FANTASY XIV Online"));
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        foreach (var parent in new[] { "", "Games", "Juegos", "01_juegos", "Program Files (x86)", "Program Files" })
            Add(Path.Combine(drive.RootDirectory.FullName, parent, "SquareEnix", "FINAL FANTASY XIV - A Realm Reborn"));
        return candidates.Order().ToArray();
    }

    public static void RequireClosed()
    {
        foreach (var name in new[] { "ffxiv_dx11", "ffxivboot64", "ffxivlauncher64", "XIVLauncher" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) throw new InvalidOperationException(UiText.Get("CloseGameFirst")); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
