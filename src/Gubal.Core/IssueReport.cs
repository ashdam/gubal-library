using System.Text.Json;

namespace GubalLibrary;

internal sealed record IssueReport(int SchemaVersion, Guid Id, string ReceiptToken,
    Guid InstallationId, string Pack, string? PackVersion, string GubalVersion,
    string? GameVersion, string Comment, string Alias, byte[]? Image)
{
    public const int MaxImageBytes = 2 * 1024 * 1024;
    public const int MaxImageDimension = 2048;
    public const int MaxCommentLength = 4000;
    public const int MaxAliasLength = 80;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record IssueReportReceipt(string Status, string? IssueUrl);
