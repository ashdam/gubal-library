using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GubalLibrary;

internal static class InstallLockOwner
{
    private const int MoreData = 234;
    private const string Indeterminate = "indeterminate";

    public static string Query(string path)
    {
        try
        {
            if (RmStartSession(out var session, 0, new StringBuilder(33)) != 0)
                return Indeterminate;

            try
            {
                if (RmRegisterResources(session, 1, [path], 0, nint.Zero, 0, nint.Zero) != 0)
                    return Indeterminate;

                uint count = 0;
                if (RmGetList(session, out var required, ref count, null, out _) != MoreData)
                    return Indeterminate;

                var processes = new ProcessInfo[required];
                count = required;
                if (RmGetList(session, out _, ref count, processes, out _) != 0 || count == 0)
                    return Indeterminate;

                var current = false;
                var other = false;
                for (var index = 0; index < count; index++)
                {
                    if (processes[index].Process.Id == Environment.ProcessId) current = true;
                    else other = true;
                }

                if (current && other) return "current-and-other-process";
                return current ? "current-process" : "other-process";
            }
            finally
            {
                _ = RmEndSession(session);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return Indeterminate;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Id;
        public FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType;
        public uint AppStatus;
        public uint SessionId;
        public int Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint appCount, nint apps, uint serviceCount, nint services);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmGetList(uint session, out uint required, ref uint count,
        [Out] ProcessInfo[]? processes, out uint rebootReasons);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint session);
}
