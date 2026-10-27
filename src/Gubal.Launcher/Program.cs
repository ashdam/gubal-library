using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GubalLibrary;

internal sealed record Settings(string GameExecutable, string Launcher, string Pack, string ExpectedGameVersion,
    LauncherAccount? Account = null);

internal static unsafe class Program
{
    private const uint Continue = 0x00010002;
    private const uint NotHandled = 0x80010001;
    private const uint DebugProcess = 0x00000001;
    private const uint UnicodeEnvironment = 0x00000400;
    private static string log = "";
    private static UsageErrorQueue? errors;
    private static volatile bool cancelled;
    private static readonly Stopwatch elapsed = Stopwatch.StartNew();

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] is not ("--launch" or "--check-host"))
                throw new ArgumentException("Use --launch or --check-host, followed by settings.json and the runtime output directory.");
            var settingsPath = Path.GetFullPath(args[1]);
            errors = new UsageErrorQueue(Path.Combine(Path.GetDirectoryName(settingsPath)!,
                UsageReporter.Endpoint.IsLoopback ? "usage-pending-local" : "usage-pending", "errors"));
            var runtime = Path.GetFullPath(args[2]);
            log = Path.Combine(Path.GetDirectoryName(settingsPath)!, "launcher.log");
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(settingsPath))
                ?? throw new InvalidDataException("Invalid settings.");
            var bootstrap = Path.Combine(runtime, "GubalStandalone.Bootstrap.dll");
            if (!File.Exists(bootstrap)) throw new FileNotFoundException("Build the native host first.", bootstrap);
            var fxr = Path.Combine(runtime, "hostfxr.dll");
            if (!File.Exists(fxr))
                fxr = Directory.GetDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "host", "fxr"))
                    .Where(p => Path.GetFileName(p).StartsWith("10.", StringComparison.Ordinal))
                    .OrderByDescending(p => Version.Parse(Path.GetFileName(p))).Select(p => Path.Combine(p, "hostfxr.dll")).First();
            var argument = new byte[4100];
            WriteString(argument, 0, fxr);
            WriteString(argument, 2048, settingsPath);
            if (args[0] == "--check-host")
            {
                var check = new ProcessStartInfo(Path.Combine(runtime, "GubalStandalone.HostCheck.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                check.ArgumentList.Add(fxr);
                check.ArgumentList.Add(settingsPath);
                using var validation = Process.Start(check) ?? throw new InvalidOperationException("Could not start the native host validation.");
                var checkOutput = validation.StandardOutput.ReadToEndAsync();
                var checkError = validation.StandardError.ReadToEndAsync();
                validation.WaitForExit();
                Console.Write(checkOutput.GetAwaiter().GetResult());
                Console.Error.Write(checkError.GetAwaiter().GetResult());
                return validation.ExitCode;
            }
            var version = File.ReadAllText(Path.Combine(Path.GetDirectoryName(settings.GameExecutable)!, "ffxivgame.ver")).Trim();
            if (string.IsNullOrWhiteSpace(version) || version != settings.ExpectedGameVersion)
                throw new InvalidDataException("The game changed during startup. Start it again from Gubal Desktop.");
            foreach (var name in new[] { "ffxiv_dx11", "ffxivboot64", "ffxivlauncher64", "XIVLauncher" })
                if (Process.GetProcessesByName(name).Length != 0)
                    throw new InvalidOperationException("Close the game and all FFXIV launchers before this probe.");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
            return Launch(settings, bootstrap, argument);
        }
        catch (Exception error)
        {
            errors?.Capture("launcher", "launch-failed", "The official launcher could not start the translated game.", error);
            Write(error.Message);
            return 1;
        }
    }

    private static int Launch(Settings settings, string bootstrap, byte[] argument)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var start = (settings.Account ?? new()).StartInfo(settings.Launcher);
        // Child processes need normal heap behavior while the startup debugger is attached.
        start.Environment["_NO_DEBUG_HEAP"] = "1";
        var environment = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
        ProcessInfo process;
        try
        {
            var command = new StringBuilder('"' + start.FileName + '"');
            foreach (var option in start.ArgumentList) command.Append(' ').Append(option);
            if (!CreateProcess(start.FileName, command, 0, 0,
                false, DebugProcess | UnicodeEnvironment, environment, start.WorkingDirectory, ref startup, out process))
                throw WindowsError("Start the official launcher");
        }
        finally { Marshal.FreeHGlobal(environment); }
        CloseHandle(process.Process);
        CloseHandle(process.Thread);
        DebugSetProcessKillOnExit(false);
        var tracked = new HashSet<uint>();
        var modules = new ConcurrentDictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        uint game = 0, mainThread = 0;
        nint gameProcess = 0, gameThread = 0;
        nint entryPoint = 0;
        byte entryByte = 0;
        var entryArmed = false;
        var completed = false;
        Task<bool>? injection = null;
        Write("Official launcher started. Sign in normally. No credentials are read or recorded.");
        try
        {
            while (!cancelled)
            {
                if (injection?.IsCompleted is true)
                {
                    var success = injection.GetAwaiter().GetResult();
                    completed = success;
                    if (!success)
                    {
                        Write("Probe initialization failed. Check runtime.log.");
                        return 3;
                    }
                    Write("Translation component loaded. The game can continue without the launcher.");
                    return 0;
                }
                if (!WaitForDebugEvent(out var e, 100))
                {
                    if (Marshal.GetLastWin32Error() == 121) continue;
                    throw new Win32Exception();
                }
                var status = Continue;
                try
                {
                    if (e.Code == 3)
                    {
                        tracked.Add(e.ProcessId);
                        string executable;
                        try
                        {
                            var path = new StringBuilder(32768);
                            var length = path.Capacity;
                            if (!QueryFullProcessImageName(e.ProcessHandle, 0, path, ref length))
                                throw WindowsError("Identify the new process");
                            executable = path.ToString();
                            if (string.Equals(executable, settings.GameExecutable, StringComparison.OrdinalIgnoreCase))
                            {
                                gameProcess = DuplicateOwnedHandle(e.ProcessHandle);
                                gameThread = DuplicateOwnedHandle(e.ThreadHandle);
                                using var image = File.OpenRead(settings.GameExecutable);
                                using var reader = new PEReader(image);
                                var offset = reader.PEHeaders.PEHeader?.AddressOfEntryPoint ?? 0;
                                if (offset == 0 || e.ImageBase == 0)
                                    throw new InvalidDataException("The game entry point could not be located.");
                                entryPoint = e.ImageBase + offset;
                            }
                        }
                        finally
                        {
                            if (e.FileHandle != 0) CloseHandle(e.FileHandle);
                        }
                        if (string.Equals(executable, settings.GameExecutable, StringComparison.OrdinalIgnoreCase))
                        {
                            game = e.ProcessId;
                            mainThread = e.ThreadId;
                            Write("FFXIV created under the probe debugger. Waiting for loader initialization.");
                        }
                    }
                    else if (e.Code == 6)
                    {
                        try
                        {
                            if (e.ProcessId == game && e.FileHandle != 0)
                            {
                                var path = new StringBuilder(32768);
                                if (GetFinalPathNameByHandle(e.FileHandle, path, (uint)path.Capacity, 0) > 0)
                                    modules[Path.GetFileName(path.ToString())] = e.ModuleBase;
                            }
                        }
                        finally { if (e.FileHandle != 0) CloseHandle(e.FileHandle); }
                    }
                    else if (e.Code == 5)
                    {
                        tracked.Remove(e.ProcessId);
                        if (e.ProcessId == game && injection is not null)
                        {
                            Write($"FFXIV exited during startup with code 0x{e.ExceptionCode:X8}.");
                            errors?.Capture("launcher", "game-exited", "FFXIV exited during startup.", exitCode: unchecked((int)e.ExceptionCode));
                            return 1;
                        }
                        if (e.ProcessId == game && injection is null)
                            throw new InvalidOperationException("FFXIV exited before probe initialization.");
                    }
                    else if (e.Code == 1)
                    {
                        status = NotHandled;
                        if (e.ProcessId == game && e.FirstChance == 0)
                        {
                            Write($"Unhandled native exception 0x{e.ExceptionCode:X8} at 0x{e.ExceptionAddress:X}.");
                            errors?.Capture("launcher", "native-exception", "FFXIV raised an unhandled native exception during startup.", exitCode: unchecked((int)e.ExceptionCode));
                            foreach (var module in modules.OrderBy(p => p.Value))
                                Write($"Module {module.Key}: 0x{module.Value:X}.");
                            try
                            {
                                var dumpPath = Path.Combine(Path.GetDirectoryName(log)!, ".cache", "startup-crash.dmp");
                                using var dump = File.Create(dumpPath);
                                if (MiniDumpWriteDump(gameProcess, game, dump.SafeFileHandle.DangerousGetHandle(), 0x1000, 0, 0, 0))
                                    Write("Startup crash dump saved in .cache/startup-crash.dmp.");
                                else Write(WindowsError("Write the startup crash dump").Message);
                            }
                            catch (Exception error) { Write(error.Message); }
                        }
                        if (e.ExceptionCode == 0x80000003 && e.ProcessId == game && injection is null)
                        {
                            if (e.ThreadId != mainThread) throw new InvalidOperationException("Unexpected startup breakpoint thread.");
                            if (!entryArmed)
                            {
                                var original = new byte[1];
                                if (!ReadProcessMemory(gameProcess, entryPoint, original, 1, out var read) || read != 1)
                                    throw WindowsError("Read the game entry point");
                                entryByte = original[0];
                                WriteCode(gameProcess, entryPoint, 0xCC);
                                entryArmed = true;
                                Write("Windows loader breakpoint reached. Waiting for the game entry point.");
                                status = Continue;
                                continue;
                            }
                            if (e.ExceptionAddress != entryPoint)
                                throw new InvalidOperationException("Unexpected breakpoint before the game entry point.");
                            WriteCode(gameProcess, entryPoint, entryByte);
                            RestoreInstructionPointer(gameThread, entryPoint);
                            entryArmed = false;
                            Write("Game entry point reached. Windows loader initialization is complete.");
                            if (SuspendThread(gameThread) == uint.MaxValue)
                                throw WindowsError("Hold the game main thread");
                            injection = Task.Run(() =>
                            {
                                var success = false;
                                try { Inject(gameProcess, modules, bootstrap, argument); success = true; return true; }
                                catch (Exception error) { Write(error.Message); return false; }
                                finally
                                {
                                    if (!success)
                                    {
                                        TerminateProcess(gameProcess, 1);
                                    }
                                    ResumeThread(gameThread);
                                }
                            });
                            status = Continue;
                        }
                        else if (e.ExceptionCode == 0x80000003 && e.ProcessId != game) status = Continue;
                    }
                }
                finally { ContinueDebugEvent(e.ProcessId, e.ThreadId, status); }
                if (tracked.Count == 0 && injection is null)
                    throw new InvalidOperationException("Launcher exited without a debuggable FFXIV child. This launch method needs adaptation.");
            }
            return 1;
        }
        finally
        {
            // A running initializer must not resume the game after the debugger has detached.
            if (!completed && gameProcess != 0)
            {
                TerminateProcess(gameProcess, 1);
            }
            foreach (var id in tracked) DebugActiveProcessStop(id);
            if (injection is null || injection.IsCompleted)
            {
                if (gameThread != 0) CloseHandle(gameThread);
                if (gameProcess != 0) CloseHandle(gameProcess);
            }
        }
    }

    private static void Inject(nint process, ConcurrentDictionary<string, nint> modules, string bootstrap, byte[] argument)
    {
        nint pathMemory = 0, argumentMemory = 0;
        var pending = false;
        try
        {
            var localKernel = NativeLibrary.Load("kernel32.dll");
            var load = NativeLibrary.GetExport(localKernel, "LoadLibraryW");
            if (!GetModuleHandleEx(6, load, out var owner)) throw new Win32Exception();
            var modulePath = new StringBuilder(1024);
            if (GetModuleFileName(owner, modulePath, modulePath.Capacity) == 0) throw new Win32Exception();
            if (!modules.TryGetValue(Path.GetFileName(modulePath.ToString()), out var remoteOwner))
                throw new InvalidOperationException("The Windows loader module was not recorded by the debugger.");
            var remoteLoad = remoteOwner + (load - owner);
            pathMemory = Allocate(process, Encoding.Unicode.GetBytes(bootstrap + '\0'));
            Write("Loading the standalone native host before the game's main thread continues.");
            pending = true;
            RunThread(process, remoteLoad, pathMemory, "Load the native DLL");
            pending = false;
            if (!modules.TryGetValue(Path.GetFileName(bootstrap), out var remoteBootstrap))
                throw new InvalidOperationException("The native probe DLL was not loaded.");
            var localBootstrap = LoadLibraryEx(bootstrap, 0, 1);
            if (localBootstrap == 0) throw new Win32Exception();
            nint start;
            try { start = remoteBootstrap + (NativeLibrary.GetExport(localBootstrap, "Start") - localBootstrap); }
            finally { NativeLibrary.Free(localBootstrap); }
            argumentMemory = Allocate(process, argument);
            pending = true;
            var result = RunThread(process, start, argumentMemory, "Initialize the standalone runtime");
            pending = false;
            if (result != 0) throw new InvalidOperationException($"Standalone initialization returned {result}.");
        }
        finally
        {
            if (!pending)
            {
                if (pathMemory != 0) VirtualFreeEx(process, pathMemory, 0, 0x8000);
                if (argumentMemory != 0) VirtualFreeEx(process, argumentMemory, 0, 0x8000);
            }
        }
    }

    private static uint RunThread(nint process, nint start, nint argument, string operation)
    {
        var thread = CreateRemoteThread(process, 0, 0, start, argument, 0, out _);
        if (thread == 0) throw WindowsError("Start the probe loading thread");
        try
        {
            if (WaitForSingleObject(thread, 60000) != 0) throw new TimeoutException($"{operation} did not finish within 60 seconds.");
            if (!GetExitCodeThread(thread, out var code)) throw new Win32Exception();
            return code;
        }
        finally { CloseHandle(thread); }
    }

    private static nint Allocate(nint process, byte[] data)
    {
        var address = VirtualAllocEx(process, 0, (nuint)data.Length, 0x3000, 4);
        if (address == 0) throw WindowsError("Allocate the probe parameters");
        if (!WriteProcessMemory(process, address, data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
        {
            var error = WindowsError("Write the probe parameters");
            VirtualFreeEx(process, address, 0, 0x8000);
            throw error;
        }
        return address;
    }

    private static void WriteCode(nint process, nint address, byte value)
    {
        if (!VirtualProtectEx(process, address, 1, 0x40, out var previous))
            throw WindowsError("Allow the temporary entry point breakpoint");
        try
        {
            if (!WriteProcessMemory(process, address, [value], 1, out var written) || written != 1)
                throw WindowsError("Update the temporary entry point breakpoint");
            if (!FlushInstructionCache(process, address, 1)) throw WindowsError("Refresh the entry point instructions");
        }
        finally { VirtualProtectEx(process, address, 1, previous, out _); }
    }

    private static void RestoreInstructionPointer(nint thread, nint entryPoint)
    {
        // Windows x64 CONTEXT requires 16-byte alignment; CONTROL includes Rip at offset 248.
        var context = (byte*)NativeMemory.AlignedAlloc(1232, 16);
        if (context == null) throw new OutOfMemoryException();
        try
        {
            new Span<byte>(context, 1232).Clear();
            *(uint*)(context + 48) = 0x00100001;
            if (!GetThreadContext(thread, context)) throw WindowsError("Read the entry point thread context");
            *(nint*)(context + 248) = entryPoint;
            if (!SetThreadContext(thread, context)) throw WindowsError("Restore the game entry point instruction");
        }
        finally { NativeMemory.AlignedFree(context); }
    }

    private static void WriteString(byte[] buffer, int offset, string value)
    {
        var text = Encoding.Unicode.GetBytes(value + '\0');
        if (text.Length > 2048) throw new ArgumentException("Probe path is too long.");
        text.CopyTo(buffer, offset);
    }

    private static void Write(string message)
    {
        message = $"[+{elapsed.Elapsed.TotalSeconds:F3}s] {message}";
        Console.WriteLine(message);
        if (log.Length > 0) { try { File.AppendAllText(log, message + Environment.NewLine); } catch { } }
    }

    private static Win32Exception WindowsError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation}: {new Win32Exception(code).Message} (Windows {code}).");
    }

    // Windows owns debug-event process and thread handles; close only our private duplicates.
    private static nint DuplicateOwnedHandle(nint source)
    {
        var current = new nint(-1);
        if (!DuplicateHandle(current, source, current, out var duplicate, 0, false, 2))
            throw WindowsError("Retain the debugger process or thread handle");
        return duplicate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public nint ReservedData, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Explicit, Size = 176)]
    private struct DebugEvent
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public nint FileHandle;
        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(24)] public nint ProcessHandle;
        [FieldOffset(24)] public nint ModuleBase;
        [FieldOffset(32)] public nint ThreadHandle;
        [FieldOffset(32)] public nint ExceptionAddress;
        [FieldOffset(40)] public nint ImageBase;
        [FieldOffset(168)] public uint FirstChance;
    }
    [DllImport("kernel32", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32", SetLastError = true)] private static extern bool WaitForDebugEvent(out DebugEvent e, uint milliseconds);
    [DllImport("kernel32")] private static extern bool ContinueDebugEvent(uint process, uint thread, uint status);
    [DllImport("kernel32")] private static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32")] private static extern bool DebugActiveProcessStop(uint id);
    [DllImport("kernel32", SetLastError = true)] private static extern uint SuspendThread(nint thread);
    [DllImport("kernel32")] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32")] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, bool inherit, uint options);
    [DllImport("kernel32", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32")] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
    [DllImport("kernel32", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] data, nuint size, out nuint written);
    [DllImport("kernel32", SetLastError = true)] private static extern bool ReadProcessMemory(nint process, nint address, byte[] data, nuint size, out nuint read);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualProtectEx(nint process, nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32", SetLastError = true)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetThreadContext(nint thread, void* context);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetThreadContext(nint thread, void* context);
    [DllImport("kernel32", SetLastError = true)] private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stack, nint start, nint argument, uint flags, out uint id);
    [DllImport("kernel32")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetExitCodeThread(nint thread, out uint code);
    [DllImport("kernel32", EntryPoint = "GetModuleHandleExW", SetLastError = true)] private static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
    [DllImport("kernel32", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(nint file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileName(nint module, StringBuilder path, int capacity);
    [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("dbghelp", SetLastError = true)] private static extern bool MiniDumpWriteDump(nint process, uint id, nint file, uint type, nint exception, nint streams, nint callback);
}
