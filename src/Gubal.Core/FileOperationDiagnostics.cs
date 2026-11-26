using System.Text.RegularExpressions;

namespace GubalLibrary;

internal static class FileOperationDiagnostics
{
    private const string ContextKey = "Gubal.FileOperation";

    public static T Run<T>(string operation, string resource, string path, Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Attach(error, operation, resource, path);
            throw;
        }
    }

    public static void Run(string operation, string resource, string path, Action action) =>
        Run(operation, resource, path, () => { action(); return true; });

    public static async Task<T> RunAsync<T>(string operation, string resource, string path, Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Attach(error, operation, resource, path);
            throw;
        }
    }

    public static void Attach(Exception error, string operation, string resource, string path, string? detail = null)
    {
        if (Describe(error).Length > 0) return;
        // Keep only the final component of an OS-reported path or the operation target.
        var reported = Regex.Match(error.GetBaseException().Message,
            "['\"](?<path>(?:[a-zA-Z]:[\\\\/]|\\\\\\\\|/)[^'\"\r\n]+)['\"]");
        var target = reported.Success ? reported.Groups["path"].Value : path;
        var name = target.Replace('\\', '/').TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length > 128) name = name[..128];
        error.Data[ContextKey] = $"Operation: {operation}; resource: {resource}; file: '{name}'. "
            + (detail is null ? "" : detail + " ");
    }

    public static string Describe(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current.Data[ContextKey] is string context) return context;
        return string.Empty;
    }
}
