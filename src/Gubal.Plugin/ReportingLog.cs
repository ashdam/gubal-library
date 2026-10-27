using Dalamud.Plugin.Services;
using Serilog;
using Serilog.Events;

namespace GubalLibrary;

internal sealed class ReportingLog(IPluginLog inner, UsageErrorQueue errors) : IPluginLog
{
    public ILogger Logger => inner.Logger;
    public LogEventLevel MinimumLogLevel { get => inner.MinimumLogLevel; set => inner.MinimumLogLevel = value; }

    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values)
    {
        inner.Write(level, exception, messageTemplate, values);
        if (level >= LogEventLevel.Error || level == LogEventLevel.Warning && exception is not null)
        {
            var message = messageTemplate;
            if (inner.Logger.BindMessageTemplate(messageTemplate, values, out var parsed, out var properties))
                message = new LogEvent(DateTimeOffset.UtcNow, level, null, parsed, properties).RenderMessage();
            errors.Capture("plugin", "operation-failed", message, exception);
        }
    }

    public void Fatal(string messageTemplate, params object[] values) => Write(LogEventLevel.Fatal, null, messageTemplate, values);
    public void Fatal(Exception? exception, string messageTemplate, params object[] values) => Write(LogEventLevel.Fatal, exception, messageTemplate, values);
    public void Error(string messageTemplate, params object[] values) => Write(LogEventLevel.Error, null, messageTemplate, values);
    public void Error(Exception? exception, string messageTemplate, params object[] values) => Write(LogEventLevel.Error, exception, messageTemplate, values);
    public void Warning(string messageTemplate, params object[] values) => Write(LogEventLevel.Warning, null, messageTemplate, values);
    public void Warning(Exception? exception, string messageTemplate, params object[] values) => Write(LogEventLevel.Warning, exception, messageTemplate, values);
    public void Information(string messageTemplate, params object[] values) => Write(LogEventLevel.Information, null, messageTemplate, values);
    public void Information(Exception? exception, string messageTemplate, params object[] values) => Write(LogEventLevel.Information, exception, messageTemplate, values);
    public void Info(string messageTemplate, params object[] values) => Information(messageTemplate, values);
    public void Info(Exception? exception, string messageTemplate, params object[] values) => Information(exception, messageTemplate, values);
    public void Debug(string messageTemplate, params object[] values) => Write(LogEventLevel.Debug, null, messageTemplate, values);
    public void Debug(Exception? exception, string messageTemplate, params object[] values) => Write(LogEventLevel.Debug, exception, messageTemplate, values);
    public void Verbose(string messageTemplate, params object[] values) => Write(LogEventLevel.Verbose, null, messageTemplate, values);
    public void Verbose(Exception? exception, string messageTemplate, params object[] values) => Write(LogEventLevel.Verbose, exception, messageTemplate, values);
}
