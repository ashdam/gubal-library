using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GubalLibrary;

internal static class PackStorage
{
    private const long MinimumReserve = 512L * 1024 * 1024;

    // Keep the download and unpacked files together, plus a reserve for allocation and other writes.
    public static long RequiredBytes(long download, long installed, bool downloaded = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(download);
        ArgumentOutOfRangeException.ThrowIfNegative(installed);
        var total = (decimal)download + installed;
        var reserve = Math.Max(MinimumReserve, decimal.Ceiling(total / 5));
        return (long)Math.Min(long.MaxValue, (downloaded ? installed : total) + reserve);
    }

    public static PackStorageStatus Check(string destination, long required)
    {
        var path = Path.GetFullPath(destination);
        try
        {
            var existing = path;
            while (!Directory.Exists(existing))
                existing = Path.GetDirectoryName(existing)
                    ?? throw new IOException("The installation drive is not available.");
            if (!Path.EndsInDirectorySeparator(existing)) existing += Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(existing, out var available, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(path, required, (long)Math.Min(available, (ulong)long.MaxValue), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(path, required, null, error.Message);
        }
    }

    public static string FormatBytes(long? bytes) => bytes is not >= 0 ? "N/A"
        : bytes >= 1_000_000_000 ? (bytes.Value / 1_000_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " GB"
        : bytes >= 1_000_000 ? (bytes.Value / 1_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " MB"
        : bytes >= 1_000 ? (bytes.Value / 1_000d).ToString("0.##", CultureInfo.CurrentCulture) + " KB"
        : bytes.Value.ToString(CultureInfo.CurrentCulture) + " B";

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}

internal readonly record struct PackStorageStatus(string Path, long RequiredBytes, long? AvailableBytes, string? Error)
{
    public bool Insufficient => this.AvailableBytes is { } available && available < this.RequiredBytes;

    public void RequireSpace()
    {
        if (this.Insufficient)
            throw new IOException($"Not enough free space for '{this.Path}'. Required: {PackStorage.FormatBytes(this.RequiredBytes)}. "
                + $"Available: {PackStorage.FormatBytes(this.AvailableBytes)}. Includes download, extraction, and a safety reserve.");
    }
}
