using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace GubalLibrary;

internal sealed class IssueReportClient(HttpClient http, Uri endpoint)
{
    public async Task<IssueReportReceipt> SubmitAsync(IssueReport report, CancellationToken cancel)
    {
        using var response = await http.PostAsJsonAsync(endpoint, report, IssueReport.Json, cancel).ConfigureAwait(false);
        return await ReadAsync(response, cancel).ConfigureAwait(false);
    }

    public async Task<IssueReportReceipt> StatusAsync(IssueReport report, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint + "/" + report.Id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", report.ReceiptToken);
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        return await ReadAsync(response, cancel).ConfigureAwait(false);
    }

    private static async Task<IssueReportReceipt> ReadAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Report request failed ({(int)response.StatusCode}).", null, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<IssueReportReceipt>(IssueReport.Json, cancel).ConfigureAwait(false)
            ?? throw new InvalidDataException("The report receipt is empty.");
        if (receipt.Status is not ("received" or "processing" or "created" or "failed"))
            throw new InvalidDataException("The report status is invalid.");
        if (receipt.IssueUrl is { } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "github.com"))
            throw new InvalidDataException("The issue address is invalid.");
        return receipt;
    }
}
