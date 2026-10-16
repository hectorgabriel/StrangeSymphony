using Symphony.Core.Config;

namespace Symphony.Core.PullRequests;

/// <summary>Portable pull request error categories, reported to the agent as tool errors.</summary>
public static class PullRequestErrorCategories
{
    public const string InvalidConfig = "invalid_pull_request_config";
    public const string MissingSecret = "missing_pull_request_secret";
    public const string Request = "pull_request_request";
    public const string Status = "pull_request_status";
    public const string Response = "pull_request_response";
}

/// <summary>Every provider failure surfaces as this exception; the message never contains the token.</summary>
public sealed class PullRequestException(string category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Category { get; } = category;
    public int? ProviderStatus { get; init; }
    public override string ToString() => $"{Category}: {Message}";
}

public sealed record PullRequestDraft(string SourceBranch, string TargetBranch, string Title, string Description, bool IsDraft);

public sealed record PullRequestInfo(string Id, string Url, bool IsDraft);

/// <summary>Creates pull requests in one repository with host-side credentials.</summary>
public interface IPullRequestProvider : IDisposable
{
    /// <summary>Longest description the provider accepts; longer ones are cut before the verification footer.</summary>
    int MaxDescriptionLength { get; }

    /// <summary>Environment names that hold the provider's credentials; stripped from agent child processes.</summary>
    IReadOnlyList<string> SecretEnvironmentNames { get; }

    /// <summary>The open pull request from <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>, if any.</summary>
    Task<PullRequestInfo?> FindActiveAsync(string sourceBranch, string targetBranch, CancellationToken ct);

    Task<PullRequestInfo> CreateAsync(PullRequestDraft draft, CancellationToken ct);
}

/// <summary>Builds providers for one <c>pull_request.kind</c>.</summary>
public interface IPullRequestProviderFactory
{
    string Kind { get; }

    /// <summary>Validates <c>pull_request.provider</c> when the workflow loads; throws <see cref="PullRequestException"/>.</summary>
    void ValidateProvider(IReadOnlyDictionary<string, object?> provider, Func<string, string?> env);

    IPullRequestProvider Create(PullRequestSettings settings, Func<string, string?> env);
}
