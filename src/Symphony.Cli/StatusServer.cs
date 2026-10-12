using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Symphony.Core.Orchestration;

namespace Symphony.Cli;

/// <summary>OPTIONAL HTTP observability extension (SPEC 13.7). Binds loopback only.</summary>
public sealed class StatusServer(Orchestrator orchestrator, ILogger<StatusServer> logger) : IAsyncDisposable
{
    private WebApplication? _app;

    public string? Url { get; private set; }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();

        _app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));
        _app.MapGet("/api/v1/state", () => Snapshot(s => Json(StateJson(s))));
        _app.MapPost("/api/v1/refresh", () =>
        {
            var r = orchestrator.RequestRefresh();
            return Json(new JsonObject
            {
                ["queued"] = r.Queued,
                ["coalesced"] = r.Coalesced,
                ["requested_at"] = r.RequestedAt.ToString("O"),
                ["operations"] = new JsonArray("poll", "reconcile"),
            }, 202);
        });
        _app.MapGet("/api/v1/{identifier}", (string identifier) => Snapshot(s =>
            s.Issues.TryGetValue(identifier, out var detail)
                ? Json(IssueJson(detail))
                : Error(404, "issue_not_found", $"issue {identifier} is not in the current runtime state")));

        await _app.StartAsync(ct);
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        logger.LogInformation("action=status_server outcome=started url={Url}", Url);
    }

    private IResult Snapshot(Func<RuntimeSnapshot, IResult> map)
    {
        try
        {
            return map(orchestrator.GetSnapshot());
        }
        catch (InvalidOperationException)
        {
            return Error(503, "unavailable", "orchestrator state is not available yet");
        }
    }

    public static JsonObject StateJson(RuntimeSnapshot s) => new()
    {
        ["generated_at"] = s.GeneratedAt.ToString("O"),
        ["counts"] = new JsonObject { ["running"] = s.Running.Count, ["retrying"] = s.Retrying.Count },
        ["running"] = new JsonArray(s.Running.Select(r => (JsonNode)RunningJson(r)).ToArray()),
        ["retrying"] = new JsonArray(s.Retrying.Select(r => (JsonNode)RetryJson(r)).ToArray()),
        ["codex_totals"] = new JsonObject
        {
            ["input_tokens"] = s.Totals.InputTokens,
            ["output_tokens"] = s.Totals.OutputTokens,
            ["total_tokens"] = s.Totals.TotalTokens,
            ["seconds_running"] = s.Totals.SecondsRunning,
        },
        ["rate_limits"] = s.RateLimits?.DeepClone(),
        ["workflow_error"] = s.WorkflowError,
    };

    private static JsonObject RunningJson(RunningRow r) => new()
    {
        ["issue_id"] = r.IssueId,
        ["issue_identifier"] = r.IssueIdentifier,
        ["issue_url"] = r.IssueUrl,
        ["state"] = r.State,
        ["session_id"] = r.SessionId,
        ["turn_count"] = r.TurnCount,
        ["last_event"] = r.LastEvent,
        ["last_message"] = r.LastMessage ?? "",
        ["started_at"] = r.StartedAt.ToString("O"),
        ["last_event_at"] = r.LastEventAt?.ToString("O"),
        ["tokens"] = new JsonObject
        {
            ["input_tokens"] = r.Tokens.InputTokens,
            ["output_tokens"] = r.Tokens.OutputTokens,
            ["total_tokens"] = r.Tokens.TotalTokens,
        },
        ["stopping"] = r.Stopping,
    };

    private static JsonObject RetryJson(RetryRow r) => new()
    {
        ["issue_id"] = r.IssueId,
        ["issue_identifier"] = r.IssueIdentifier,
        ["issue_url"] = r.IssueUrl,
        ["attempt"] = r.Attempt,
        ["due_at"] = r.DueAt.ToString("O"),
        ["error"] = r.Error,
    };

    private static JsonObject IssueJson(IssueDetail d) => new()
    {
        ["issue_identifier"] = d.IssueIdentifier,
        ["issue_id"] = d.IssueId,
        ["status"] = d.Status,
        ["workspace"] = new JsonObject { ["path"] = d.WorkspacePath },
        ["attempts"] = new JsonObject { ["restart_count"] = d.RestartCount, ["current_retry_attempt"] = d.CurrentRetryAttempt },
        ["running"] = d.Running is null ? null : RunningJson(d.Running),
        ["retry"] = d.Retry is null ? null : RetryJson(d.Retry),
        ["recent_events"] = new JsonArray(d.RecentEvents.Select(e => (JsonNode)new JsonObject
        {
            ["at"] = e.At.ToString("O"),
            ["event"] = e.Event,
            ["message"] = e.Message,
        }).ToArray()),
        ["last_error"] = d.LastError,
        ["tracked"] = new JsonObject(),
    };

    private static IResult Json(JsonNode node, int status = 200) =>
        Results.Content(node.ToJsonString(), "application/json", System.Text.Encoding.UTF8, status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
