namespace GubalLibrary;

internal interface IPackInstallLog
{
    void Information(string message, params object[] values);
    void Warning(string message, params object[] values);
    void Warning(Exception error, string message, params object[] values);
    void Error(Exception error, string message, params object[] values);
}
