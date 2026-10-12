using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Core.Tracker;

namespace Symphony.AzureDevOps;

/// <summary>
/// Thin Azure DevOps REST client. Every failure surfaces as <see cref="TrackerException"/> with a
/// portable category; the PAT is never included in messages.
/// </summary>
public sealed class AzureDevOpsClient : IDisposable
{
    public const int BatchLimit = 200;

    private readonly HttpClient _http;
    private readonly AzureDevOpsOptions _options;

    public AzureDevOpsClient(AzureDevOpsOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = options.OrganizationUrl,
            Timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.Token)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Symphony/1.0");
    }

    private string P => Uri.EscapeDataString(_options.Project);
    private string V => _options.ApiVersion;

    /// <summary>WIQL page of work item ids (ascending).</summary>
    public async Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int top, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = wiql };
        var json = await SendAsync(HttpMethod.Post, $"{P}/_apis/wit/wiql?$top={top}&api-version={V}", body, null, ct);
        if (json?["workItems"] is not JsonArray items)
        {
            throw new TrackerException(TrackerErrorCategories.Response, "WIQL response missing workItems");
        }

        var ids = new List<int>(items.Count);
        foreach (var item in items)
        {
            if (item?["id"]?.GetValueKind() != JsonValueKind.Number)
            {
                throw new TrackerException(TrackerErrorCategories.Response, "WIQL response contains a work item without a numeric id");
            }

            ids.Add(item["id"]!.GetValue<int>());
        }

        return ids;
    }

    /// <summary>Batch get (max 200 ids). Unknown/inaccessible ids are omitted (errorPolicy=Omit).</summary>
    public async Task<IReadOnlyList<JsonObject>> GetWorkItemsAsync(IReadOnlyList<int> ids, bool expandRelations, IReadOnlyList<string>? fields, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        if (ids.Count > BatchLimit) throw new ArgumentOutOfRangeException(nameof(ids), "max 200 ids per batch");

        var body = new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
            ["errorPolicy"] = "Omit",
        };
        if (expandRelations) body["$expand"] = "Relations";
        if (fields is { Count: > 0 }) body["fields"] = new JsonArray(fields.Select(f => (JsonNode)f).ToArray());

        var json = await SendAsync(HttpMethod.Post, $"{P}/_apis/wit/workitemsbatch?api-version={V}", body, null, ct);
        if (json?["value"] is not JsonArray value)
        {
            throw new TrackerException(TrackerErrorCategories.Response, "work item batch response missing value");
        }

        return value.OfType<JsonObject>().ToList();
    }

    public async Task<string?> GetAuthenticatedUserIdAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "_apis/connectionData?api-version=7.1-preview", null, null, ct);
        return json?["authenticatedUser"]?["id"]?.GetValue<string>();
    }

    public Task<JsonNode?> PatchWorkItemAsync(int id, JsonArray patch, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{P}/_apis/wit/workitems/{id}?api-version={V}", patch, "application/json-patch+json", ct);

    public Task<JsonNode?> AddCommentAsync(int id, string markdown, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{P}/_apis/wit/workItems/{id}/comments?format=markdown&api-version=7.1-preview.4",
            new JsonObject { ["text"] = markdown }, null, ct);

    public Task<JsonNode?> GetCommentsAsync(int id, int top, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{P}/_apis/wit/workItems/{id}/comments?$top={top}&order=desc&api-version=7.1-preview.4", null, null, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string relative, JsonNode? body, string? contentType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relative);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new TrackerException(TrackerErrorCategories.Request, $"Azure DevOps request failed: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                throw new TrackerException(TrackerErrorCategories.RateLimited, "Azure DevOps rate limit (429)")
                {
                    ProviderStatus = 429,
                    RetryAfterMs = retryAfter is { } d ? (int)d.TotalMilliseconds : null,
                };
            }

            // 203 + HTML is how Azure DevOps answers an invalid/expired PAT.
            var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation
                || (response.IsSuccessStatusCode && !isJson && text.Length > 0))
            {
                throw new TrackerException(TrackerErrorCategories.Status,
                    $"Azure DevOps authentication/authorization failed ({(int)response.StatusCode}); check the PAT and its Work Items scope")
                {
                    ProviderStatus = (int)response.StatusCode,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TrackerException(TrackerErrorCategories.Status,
                    $"Azure DevOps returned {(int)response.StatusCode}: {ErrorMessage(text)}")
                {
                    ProviderStatus = (int)response.StatusCode,
                };
            }

            if (text.Length == 0) return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new TrackerException(TrackerErrorCategories.Response, "Azure DevOps returned malformed JSON", ex);
            }
        }
    }

    private static string ErrorMessage(string text)
    {
        try
        {
            if (JsonNode.Parse(text)?["message"]?.GetValue<string>() is { } m) return m;
        }
        catch (JsonException)
        {
        }

        return text.Length > 300 ? text[..300] : text;
    }

    public void Dispose() => _http.Dispose();
}
