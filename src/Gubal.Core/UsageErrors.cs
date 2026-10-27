using System.Text.Json;
using System.Text.RegularExpressions;

namespace GubalLibrary;

internal sealed record UsageError(Guid Id, DateTimeOffset OccurredAt, string GubalVersion,
    string Stage, string Code, string Message, string? ExceptionType, int? HResult, int? ExitCode);

internal sealed class UsageErrorQueue
{
    private readonly string directory;
    private string[] previous = [];
    private readonly HashSet<string> captured = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public UsageErrorQueue(string directory)
    {
        this.directory = directory;
        BeginSession();
    }

    public void BeginSession()
    {
        try { previous = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Order().ToArray() : []; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { previous = []; }
    }

    public void Capture(string stage, string code, string message, Exception? exception = null, int? exitCode = null)
    {
        if (exception is OperationCanceledException) return;
        var cause = exception?.GetBaseException();
        var detail = Clean(message + (cause is null ? "" : " " + cause.Message));
        var fingerprint = $"{stage}|{code}|{detail}|{cause?.GetType().FullName}|{cause?.HResult}|{exitCode}";
        lock (gate)
        {
            if (!captured.Add(fingerprint)) return;
            try
            {
                Directory.CreateDirectory(directory);
                var error = new UsageError(Guid.NewGuid(), DateTimeOffset.UtcNow,
                    typeof(UsageError).Assembly.GetName().Version!.ToString(), stage, code, detail,
                    cause?.GetType().FullName, cause?.HResult, exitCode);
                var path = Path.Combine(directory, error.Id + ".json");
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(error));
                File.Move(path + ".tmp", path);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                captured.Remove(fingerprint);
            }
        }
    }

    public IEnumerable<UsageError> Pending()
    {
        foreach (var path in previous)
        {
            UsageError? error;
            try { error = JsonSerializer.Deserialize<UsageError>(File.ReadAllText(path)); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (error is not null) yield return error;
        }
    }

    public void Acknowledge(UsageError error) => File.Delete(Path.Combine(directory, error.Id + ".json"));

    internal static string Clean(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:https?|file)://[^\s<>""']+", "[url]");
        text = Regex.Replace(text, @"(?i)(?:[a-z]:\\|\\\\)[^\r\n""'<>]*", "[path]");
        text = Regex.Replace(text, @"(?<!\w)/(?:[^\s/]+/)+[^\s]*", "[path]");
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}", "[email]");
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;]+", "[redacted]");
        text = Regex.Replace(text, @"(?i)\b(?:password|passwd|token|secret|authorization|session_ticket|sqexid|username|otp)\s*[:=]\s*[^\r\n,;]+", "[redacted]");
        text = Regex.Replace(text, @"[\p{Cc}\p{Cf}]+", " ").Trim();
        return text.Length > 512 ? text[..512] : text;
    }
}
