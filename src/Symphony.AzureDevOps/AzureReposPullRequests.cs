using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Core.Config;
using Symphony.Core.PullRequests;

namespace Symphony.AzureDevOps;

/// <summary>Resolved <c>pull_request.provider</c> settings for <c>pull_request.kind: azure_devops</c>.</summary>
public sealed record AzureReposOptions
{
    public required Uri OrganizationUrl { get; init; }
    public required string Project { get; init; }
    public required string Repository { get; init; }
    public required string Token { get; init; }
    public string? TokenEnvName { get; init; }
    public string ApiVersion { get; init; } = "7.1";
    public int RequestTimeoutMs { get; init; } = 30000;

    public static AzureReposOptions Parse(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env)
    {
        var (org, _) = ConfigValues.ResolveEnvReference(Str(provider, "organization_url"), env);
        if (string.IsNullOrWhiteSpace(org))
        {
            throw Invalid("pull_request.provider.organization_url is required (for example https://dev.azure.com/contoso)");
        }

        if (!Uri.TryCreate(org.TrimEnd('/'), UriKind.Absolute, out var orgUri) || (orgUri.Scheme != Uri.UriSchemeHttps && orgUri.Scheme != Uri.UriSchemeHttp))
        {
            throw Invalid("pull_request.provider.organization_url must be an absolute http(s) URL");
        }

        var (project, _) = ConfigValues.ResolveEnvReference(Str(provider, "project"), env);
        if (string.IsNullOrWhiteSpace(project))
        {
            throw Invalid("pull_request.provider.project is required");
        }

        var (repository, _) = ConfigValues.ResolveEnvReference(Str(provider, "repository"), env);
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw Invalid("pull_request.provider.repository is required (the repository name or id)");
        }

        // Same rules as the tracker's token: $VAR, a literal (discouraged), or AZURE_DEVOPS_PAT when omitted.
        var tokenRaw = Str(provider, "token");
        var (token, tokenEnv) = tokenRaw is null
            ? (env(AzureDevOpsOptions.DefaultTokenEnv), AzureDevOpsOptions.DefaultTokenEnv)
            : ConfigValues.ResolveEnvReference(tokenRaw, env);
        if (string.IsNullOrEmpty(token))
        {
            throw new PullRequestException(PullRequestErrorCategories.MissingSecret,
                tokenEnv is null
                    ? "pull_request.provider.token is empty"
                    : $"pull_request.provider.token: environment variable {tokenEnv} is unset or empty");
        }

        var timeout = provider.TryGetValue("request_timeout_ms", out var t) && t is not null
            ? t switch
            {
                long l when l >= 1000 && l <= int.MaxValue => (int)l,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p >= 1000 => p,
                _ => throw Invalid("pull_request.provider.request_timeout_ms must be an integer >= 1000"),
            }
            : 30000;

        return new AzureReposOptions
        {
            OrganizationUrl = new Uri(orgUri.ToString().TrimEnd('/') + "/"),
            Project = project.Trim(),
            Repository = repository.Trim(),
            Token = token,
            TokenEnvName = tokenEnv,
            ApiVersion = Str(provider, "api_version") ?? "7.1",
            RequestTimeoutMs = timeout,
        };
    }

    private static string? Str(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is not null
            ? v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture)
            : null;

    private static PullRequestException Invalid(string message) => new(PullRequestErrorCategories.InvalidConfig, message);
}

public sealed class AzureReposPullRequestProviderFactory : IPullRequestProviderFactory
{
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public AzureReposPullRequestProviderFactory() : this(() => new HttpClientHandler())
    {
    }

    public AzureReposPullRequestProviderFactory(Func<HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public string Kind => "azure_devops";

    public void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env) =>
        AzureReposOptions.Parse(provider, env);

    public IPullRequestProvider Create(PullRequestSettings settings, Func<string, string?> env) =>
        new AzureReposPullRequestProvider(AzureReposOptions.Parse(settings.Provider, env), _handlerFactory());
}

/// <summary>
/// Azure Repos pull requests (REST 7.1). Needs a PAT with the <i>Code: Read &amp; Write</i> scope; the
/// token is sent only to the configured organization and never appears in an error message.
/// </summary>
public sealed class AzureReposPullRequestProvider : IPullRequestProvider
{
    private readonly HttpClient _http;
    private readonly HttpMessageHandler _handler;
    private readonly AzureReposOptions _options;

    public AzureReposPullRequestProvider(AzureReposOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _handler = handler;
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

    /// <summary>Azure Repos rejects longer descriptions.</summary>
    public int MaxDescriptionLength => 4000;

    public IReadOnlyList<string> SecretEnvironmentNames =>
        _options.TokenEnvName is { } name
            ? AzureDevOpsOptions.KnownSecretEnvNames.Append(name).Distinct(StringComparer.Ordinal).ToList()
            : AzureDevOpsOptions.KnownSecretEnvNames;

    private string RepoPath =>
        $"{Uri.EscapeDataString(_options.Project)}/_apis/git/repositories/{Uri.EscapeDataString(_options.Repository)}/pullrequests";

    public async Task<PullRequestInfo?> FindActiveAsync(string sourceBranch, string targetBranch, CancellationToken ct)
    {
        var query = $"searchCriteria.sourceRefName={Uri.EscapeDataString(RefName(sourceBranch))}"
            + $"&searchCriteria.targetRefName={Uri.EscapeDataString(RefName(targetBranch))}"
            + $"&searchCriteria.status=active&$top=1&api-version={_options.ApiVersion}";
        var json = await SendAsync(HttpMethod.Get, $"{RepoPath}?{query}", null, ct);
        if (json?["value"] is not JsonArray value)
        {
            throw new PullRequestException(PullRequestErrorCategories.Response, "pull request list response missing value");
        }

        return value.OfType<JsonObject>().Select(Info).FirstOrDefault();
    }

    public async Task<PullRequestInfo> CreateAsync(PullRequestDraft draft, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["sourceRefName"] = RefName(draft.SourceBranch),
            ["targetRefName"] = RefName(draft.TargetBranch),
            ["title"] = draft.Title,
            ["description"] = draft.Description,
            ["isDraft"] = draft.IsDraft,
        };
        var json = await SendAsync(HttpMethod.Post, $"{RepoPath}?api-version={_options.ApiVersion}", body, ct);
        return json is JsonObject created
            ? Info(created)
            : throw new PullRequestException(PullRequestErrorCategories.Response, "pull request create response is not an object");
    }

    private PullRequestInfo Info(JsonObject pr)
    {
        if (pr["pullRequestId"] is not JsonValue idValue || !idValue.TryGetValue(out int id))
        {
            throw new PullRequestException(PullRequestErrorCategories.Response, "pull request response has no pullRequestId");
        }

        var isDraft = pr["isDraft"] is JsonValue d && d.TryGetValue(out bool draft) && draft;
        // The API's own "url" points at the REST resource; reviewers need the web page.
        var web = new Uri(_options.OrganizationUrl,
            $"{Uri.EscapeDataString(_options.Project)}/_git/{Uri.EscapeDataString(_options.Repository)}/pullrequest/{id.ToString(CultureInfo.InvariantCulture)}");
        // AbsoluteUri, not ToString(): the latter unescapes, and "My Project" would break the link.
        return new PullRequestInfo(id.ToString(CultureInfo.InvariantCulture), web.AbsoluteUri, isDraft);
    }

    private static string RefName(string branch) =>
        branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private async Task<JsonNode?> SendAsync(HttpMethod method, string relative, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relative);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
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
            throw new PullRequestException(PullRequestErrorCategories.Request, $"Azure DevOps request failed: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            // 203 + HTML is how Azure DevOps answers an invalid or expired PAT.
            var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation
                || (response.IsSuccessStatusCode && !isJson && text.Length > 0))
            {
                throw new PullRequestException(PullRequestErrorCategories.Status,
                    $"Azure DevOps authentication/authorization failed ({(int)response.StatusCode}); check the PAT and its Code (Read & Write) scope")
                {
                    ProviderStatus = (int)response.StatusCode,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PullRequestException(PullRequestErrorCategories.Status,
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
                throw new PullRequestException(PullRequestErrorCategories.Response, "Azure DevOps returned malformed JSON", ex);
            }
        }
    }

    private static string ErrorMessage(string text)
    {
        try
        {
            if (JsonNode.Parse(text)?["message"]?.GetValue<string>() is { } m) return m;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }

        return text.Length > 300 ? text[..300] : text;
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}
