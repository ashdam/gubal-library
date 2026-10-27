using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CheapLoc;
using FFXIVClientStructs.FFXIV.Client.System.File;
using GubalLibrary;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using GameFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace GubalStandalone;

internal sealed record Settings(string GameExecutable, string Launcher, string Pack, string ExpectedGameVersion,
    string Mode = "redirect", string Scope = "all-resources");

public static unsafe class Entry
{
    private static readonly object LogGate = new();
    private static string logPath = "";
    private static UsageErrorQueue? errors;
    private static Dictionary<string, string> pages = new(StringComparer.OrdinalIgnoreCase);
    private static IHook<FileThread.Delegates.DoFileJob>? hook;
    private static int served;
    private static int reads;
    private static int started;
    private static readonly Stopwatch elapsed = Stopwatch.StartNew();
    private static bool redirect;
    private static AssetResources? assets;
    private static delegate* unmanaged<FileThread*, FileDescriptor*, int, byte, byte> readFile;
    private static string[] disabledParts = [];

    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--check") return 2;
        try
        {
            var path = Path.GetFullPath(args[1]);
            Prepare(path);
            File.WriteAllText(Path.ChangeExtension(path, ".compatibility.json"), JsonSerializer.Serialize(new { DisabledParts = disabledParts }));
            return 0;
        }
        catch (NativeCompatibilityException error) { ReportError("compatibility", error); Console.Error.WriteLine(error.Message); return 3; }
        catch (Exception error) { ReportError("compatibility", error); Console.Error.WriteLine(error.Message); return 1; }
    }

    public static int Validate(nint argument, int bytes)
    {
        try { Prepare(Marshal.PtrToStringUni(argument) ?? throw new ArgumentException("Missing settings.")); return 0; }
        catch (Exception error) { ReportError("host-check", error); return 1; }
    }

    public static int Initialize(nint argument, int bytes)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return 2;
        try
        {
            var settingsPath = Marshal.PtrToStringUni(argument) ?? throw new ArgumentException("Missing settings.");
            var settings = Prepare(settingsPath);
            if (Process.GetCurrentProcess().ProcessName != "ffxiv_dx11")
                throw new InvalidOperationException("The hook can only run inside ffxiv_dx11.");
            if (Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m =>
                m.ModuleName.StartsWith("Dalamud", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Do not load this probe with Dalamud.");
            if (settings.Mode == "runtime-only")
            {
                Log("READY: runtime only; no file hook installed. The game remains in English.");
                return 0;
            }
            redirect = settings.Mode == "redirect";
            InteropGenerator.Runtime.Resolver.GetInstance.Setup();
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            InteropGenerator.Runtime.Resolver.GetInstance.Resolve();
            var address = FileThread.Addresses.DoFileJob.Value;
            if (address == 0) throw new InvalidOperationException("The file read signature was not found.");
            if (redirect && pages.Keys.Any(AssetResources.IsAsset))
            {
                var signatures = new ImageSignatures(settings.GameExecutable, Process.GetCurrentProcess().MainModule!.BaseAddress);
                if (!signatures.TryScanText(AssetResources.ReadFileSignature, out var readAddress))
                    throw new InvalidOperationException("The loose-file read signature was not found.");
                readFile = (delegate* unmanaged<FileThread*, FileDescriptor*, int, byte, byte>)readAddress;
                assets = new AssetResources(pages, signatures, new RuntimeLog(Log));
            }
            hook = ReloadedHooks.Instance.CreateHook<FileThread.Delegates.DoFileJob>(Read, address);
            hook.Activate();
            Log($"READY: {pages.Count} pages; read hook 0x{address:X}.");
            return 0;
        }
        catch (Exception error) { ReportError("initialization", error); return 1; }
    }

    private static Settings Prepare(string settingsPath)
    {
        logPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "runtime.log");
        errors ??= new UsageErrorQueue(Path.Combine(Path.GetDirectoryName(settingsPath)!,
            UsageReporter.Endpoint.IsLoopback ? "usage-pending-local" : "usage-pending", "errors"));
        Loc.SetupWithFallbacks();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(settingsPath))
            ?? throw new InvalidDataException("Invalid settings.");
        if (settings.Mode is not ("runtime-only" or "passthrough" or "redirect"))
            throw new InvalidDataException("Mode must be runtime-only, passthrough or redirect.");
        if (settings.Scope is not ("lobby-addon" or "all-text" or "all-resources"))
            throw new InvalidDataException("Scope must be lobby-addon, all-text or all-resources.");
        var gameVersion = File.ReadAllText(Path.Combine(Path.GetDirectoryName(settings.GameExecutable)!, "ffxivgame.ver")).Trim();
        if (string.IsNullOrWhiteSpace(gameVersion) || gameVersion != settings.ExpectedGameVersion)
            throw new InvalidDataException("The game changed during startup. Start it again from Gubal Desktop.");
        NativeCompatibility.Check(settings.GameExecutable, includeAssets: false);
        var manifest = PackManifest.Read(settings.Pack).Manifest ?? throw new InvalidDataException("No pack manifest.");
        var result = PackResourceCompatibility.Check(settings.Pack, manifest, gameVersion,
            Path.Combine(Path.GetDirectoryName(settings.GameExecutable)!, "sqpack"),
            Path.Combine(Path.GetDirectoryName(settingsPath)!, ".cache", "compatibility"), () => true, Log);
        if (result.Error is not null) throw new InvalidDataException(result.Error);
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textDirectory = Path.Combine(settings.Pack, "exd");
        var files = settings.Scope != "lobby-addon"
            ? Directory.GetFiles(textDirectory, "*.exd", SearchOption.AllDirectories)
            : new[] { "lobby", "addon" }.SelectMany(sheet =>
                Directory.GetFiles(textDirectory, $"{sheet}_*_en.exd")).ToArray();
        if (files.Length == 0) throw new InvalidDataException("No text pages in the pack.");
        var layoutDirectory = Path.Combine(settings.Pack, "ui", "uld");
        if (settings.Scope != "lobby-addon" && Directory.Exists(layoutDirectory))
            files = files.Concat(Directory.GetFiles(layoutDirectory, "*.uld", SearchOption.AllDirectories)).ToArray();
        if (settings.Scope == "all-resources")
            files = files.Concat(Directory.EnumerateFiles(settings.Pack, "*", SearchOption.AllDirectories)
                .Where(AssetResources.IsAsset)).ToArray();
        foreach (var file in files)
        {
            var local = Path.GetFullPath(file);
            if (local.Length > 259) throw new InvalidDataException("Pack path exceeds the native path limit.");
            selected.Add(Path.GetRelativePath(settings.Pack, local).Replace('\\', '/'), local);
        }
        if (result.Selection is { } compatibility)
        {
            PackResourceCompatibility.Restrict(compatibility, selected.Keys);
            if (compatibility.RequiredFailed)
                throw new InvalidDataException("The pack's required resources are incompatible. Update the language pack.");
            var refused = selected.Keys.Where(p => !compatibility.Allows(p)).ToArray();
            var disabledSheets = refused.Select(path => path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)
                ? "addon" : PackParts.SheetOf(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            disabledParts = PackParts.Groups.SelectMany(group => group.Parts
                .Where(part => part.Sheets.Any(disabledSheets.Contains))
                .Select(part => group.Name + " > " + part.Name)).Distinct().ToArray();
            if (refused.Length > 0 && disabledParts.Length == 0)
                disabledParts = [PackParts.UncategorizedGroupName];
            foreach (var path in refused)
            {
                selected.Remove(path);
                Log($"ORIGINAL: {path}; incompatible source {compatibility.Excluded[path]}.");
            }
            if (refused.Length > 0)
                Console.WriteLine($"Compatibility mode: {refused.Length} resources use the game originals.");
        }
        if (selected.Count == 0) throw new InvalidDataException("No compatible resources in the selected scope.");
        pages = selected;
        if (pages.Keys.Any(AssetResources.IsAsset))
            NativeCompatibility.Check(settings.GameExecutable, includeAssets: true);
        var pageCount = pages.Keys.Count(path => path.EndsWith(".exd", StringComparison.OrdinalIgnoreCase));
        var layoutCount = pages.Keys.Count(path => path.EndsWith(".uld", StringComparison.OrdinalIgnoreCase));
        var assetCount = pages.Keys.Count(AssetResources.IsAsset);
        var summary = $"Game {gameVersion}; pack {manifest.TranslationVersion}; {pageCount} text pages, {layoutCount} layouts and {assetCount} texture/font files ({settings.Scope}).";
        Console.WriteLine(summary);
        Log(summary);
        return settings;
    }

    // Keep this descriptor route aligned with ExdRedirector.Serve and its restoration block.
    private static byte Read(FileThread* thread, FileDescriptor* descriptor, int priority, bool isSync)
    {
        var number = Interlocked.Increment(ref reads);
        if (number <= 5) Log($"READ {number}: thread 0x{(nint)thread:X}, descriptor 0x{(nint)descriptor:X}.");
        string? local = null;
        var asset = false;
        try
        {
            if (redirect && descriptor != null && descriptor->ResourceHandle != null)
            {
                var name = descriptor->ResourceHandle->FileName.AsSpan();
                if (name.EndsWith(".exd"u8) || name.EndsWith(".uld"u8))
                    pages.TryGetValue(Encoding.UTF8.GetString(name), out local);
                else if (assets is not null)
                    asset = assets.RootedPaths.TryGetValue(Encoding.UTF8.GetString(name), out local);
            }
        }
        catch (Exception error) { ReportError("file-read", error); }
        if (local is null)
        {
            if (number <= 5) Log($"PASS {number}: calling the original reader.");
            var original = hook!.OriginalFunction(thread, descriptor, priority, isSync);
            if (number <= 5) Log($"PASS {number}: original reader returned {original}.");
            return original;
        }
        var originalMode = descriptor->FileMode;
        var originalScratch = *(nint*)((byte*)descriptor + 0x30);
        Span<char> originalPath = stackalloc char[260];
        descriptor->FilePath.CopyTo(originalPath);
        try
        {
            var scratch = stackalloc byte[0x21 + (local.Length + 1) * 2];
            new Span<byte>(scratch, 0x21 + (local.Length + 1) * 2).Clear();
            local.AsSpan().CopyTo(new Span<char>(scratch + 0x21, local.Length));
            local.AsSpan().CopyTo(descriptor->FilePath);
            descriptor->FilePath[local.Length] = '\0';
            *(byte**)((byte*)descriptor + 0x30) = scratch;
            descriptor->FileMode = GameFileMode.LoadUnpackedResource;
            var result = asset ? readFile(thread, descriptor, priority, isSync ? (byte)1 : (byte)0)
                : hook!.OriginalFunction(thread, descriptor, priority, isSync);
            if (Interlocked.Increment(ref served) <= 12) Log($"SERVED: {Path.GetFileName(local)}; result {result}.");
            return result;
        }
        finally
        {
            descriptor->FileMode = originalMode;
            *(nint*)((byte*)descriptor + 0x30) = originalScratch;
            originalPath.CopyTo(descriptor->FilePath);
        }
    }

    private static void ReportError(string stage, Exception error)
    {
        errors?.Capture(stage, "runtime-failed", "The Desktop translation component failed.", error);
        Log(error.ToString());
    }

    private static void Log(string message)
    {
        try { lock (LogGate) { if (logPath.Length > 0) File.AppendAllText(logPath,
            $"[PID {Environment.ProcessId}, +{elapsed.Elapsed.TotalSeconds:F3}s] {message}" + Environment.NewLine); } }
        catch { }
    }
}
