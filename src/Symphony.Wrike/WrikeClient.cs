using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Core.Tracker;

namespace Symphony.Wrike;

/// <summary>
/// Thin Wrike API v4 client. Every failure surfaces as <see cref="TrackerException"/> with a
/// portable category; the token is never included in messages.
/// </summary>
public sealed class WrikeClient : IDisposable
{
    public const int BatchLimit = 100;

    /// <summary><see cref="Exception.Data"/> key holding Wrike's <c>error</c> code (for example <c>invalid_request</c>).</summary>
    public const string ErrorCodeKey = "wrike_error";

    private readonly HttpClient _http;

    public WrikeClient(WrikeOptions options, HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = options.ApiBase,
            Timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Symphony/1.0");
    }

    /// <summary>
    /// Batch get (max 100 ids) with Wrike's default fields, which include <c>subTaskIds</c>, <c>superTaskIds</c>,
    /// <c>dependencyIds</c> and <c>responsibleIds</c> but not <c>description</c>. One unknown id fails the whole batch.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> GetTasksAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        if (ids.Count > BatchLimit) throw new ArgumentOutOfRangeException(nameof(ids), "max 100 ids per batch");
        return Data(await SendAsync(HttpMethod.Get, $"tasks/{string.Join(",", ids.Select(Path))}", null, ct), "tasks");
    }

    /// <summary>Single task; unlike the batch call this includes <c>description</c>.</summary>
    public async Task<JsonObject?> GetTaskAsync(string id, CancellationToken ct) =>
        Data(await SendAsync(HttpMethod.Get, $"tasks/{Path(id)}", null, ct), "task").FirstOrDefault();

    public async Task<IReadOnlyList<JsonObject>> GetWorkflowsAsync(CancellationToken ct) =>
        Data(await SendAsync(HttpMethod.Get, "workflows", null, ct), "workflows");

    public async Task<string?> GetMyContactIdAsync(CancellationToken ct) =>
        WrikeNormalizer.Str(Data(await SendAsync(HttpMethod.Get, "contacts?me=true", null, ct), "contacts").FirstOrDefault()?["id"]);

    /// <summary>Converts a numeric permalink id to the v4 task id.</summary>
    public async Task<string?> ResolvePermalinkIdAsync(string permalinkId, CancellationToken ct) =>
        WrikeNormalizer.Str(Data(await SendAsync(HttpMethod.Get, $"ids?ids=%5B{permalinkId}%5D&type=ApiV2Task", null, ct), "ids").FirstOrDefault()?["id"]);

    public async Task<IReadOnlyList<JsonObject>> GetDependenciesAsync(string taskId, CancellationToken ct) =>
        Data(await SendAsync(HttpMethod.Get, $"tasks/{Path(taskId)}/dependencies", null, ct), "dependencies");

    public async Task<IReadOnlyList<JsonObject>> GetCommentsAsync(string taskId, CancellationToken ct) =>
        Data(await SendAsync(HttpMethod.Get, $"tasks/{Path(taskId)}/comments", null, ct), "comments");

    public async Task<JsonObject?> AddCommentAsync(string taskId, string html, CancellationToken ct) =>
        Data(await SendAsync(HttpMethod.Post, $"tasks/{Path(taskId)}/comments", new() { ["text"] = html }, ct), "comment").FirstOrDefault();

    public async Task<JsonObject?> SetCustomStatusAsync(string taskId, string customStatusId, CancellationToken ct) =>
        Data(await SendAsync(HttpMethod.Put, $"tasks/{Path(taskId)}", new() { ["customStatus"] = customStatusId }, ct), "task").FirstOrDefault();

    /// <summary>True for Wrike's answer to an id that does not exist or is not visible to the token.</summary>
    public static bool IsInvalidId(TrackerException ex) =>
        ex.ProviderStatus is 400 or 404 && ex.Data[ErrorCodeKey] is "invalid_request" or "not_found" or "resource_not_found";

    private static string Path(string id) =>
        WrikeOptions.IsTaskId(id) ? id : throw new TrackerException(TrackerErrorCategories.Request, "invalid Wrike id");

    private static List<JsonObject> Data(JsonNode? json, string what) =>
        json?["data"] is JsonArray data
            ? data.OfType<JsonObject>().ToList()
            : throw new TrackerException(TrackerErrorCategories.Response, $"Wrike {what} response missing data");

    private async Task<JsonNode?> SendAsync(HttpMethod method, string relative, Dictionary<string, string>? form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relative);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
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
            throw new TrackerException(TrackerErrorCategories.Request, $"Wrike request failed: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var header = response.Headers.RetryAfter;
                var retryAfter = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                throw new TrackerException(TrackerErrorCategories.RateLimited, "Wrike rate limit (429)")
                {
                    ProviderStatus = 429,
                    RetryAfterMs = retryAfter is { } d ? (int)Math.Max(0, d.TotalMilliseconds) : null,
                };
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new TrackerException(TrackerErrorCategories.Status,
                    $"Wrike authentication/authorization failed ({(int)response.StatusCode}); check the token and its access to the queue task")
                {
                    ProviderStatus = (int)response.StatusCode,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                var (code, description) = Error(text);
                var ex = new TrackerException(TrackerErrorCategories.Status,
                    $"Wrike returned {(int)response.StatusCode}{(code is null ? "" : " " + code)}: {description}")
                {
                    ProviderStatus = (int)response.StatusCode,
                };
                ex.Data[ErrorCodeKey] = code;
                throw ex;
            }

            if (text.Length == 0) return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new TrackerException(TrackerErrorCategories.Response, "Wrike returned malformed JSON", ex);
            }
        }
    }

    private static (string? Code, string Description) Error(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject o)
            {
                return (WrikeNormalizer.Str(o["error"]), WrikeNormalizer.Str(o["errorDescription"]) ?? "no description");
            }
        }
        catch (JsonException)
        {
        }

        return (null, text.Length > 300 ? text[..300] : text);
    }

    public void Dispose() => _http.Dispose();
}
