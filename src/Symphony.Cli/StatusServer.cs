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
    /// <summary>
    /// Loopback names only. Binding to 127.0.0.1 does not stop DNS rebinding: a hostile page whose
    /// domain re-resolves to 127.0.0.1 reaches this server with its own name in the Host header.
    /// </summary>
    public static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    private WebApplication? _app;

    public string? Url { get; private set; }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddHostFiltering(o =>
        {
            o.AllowedHosts = AllowedHosts;
            o.AllowEmptyHosts = false;
            o.IncludeFailureMessage = false;
        });
        _app = builder.Build();
        _app.UseHostFiltering();
        _app.Use(async (ctx, next) =>
        {
            // Host filtering does not stop a hostile page from POSTing a form to 127.0.0.1 (the
            // Host header is genuine). Browsers always send Origin on such requests; non-browser
            // clients usually omit it, so only a present, foreign Origin is rejected.
            var method = ctx.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
                && ctx.Request.Headers.Origin.Count > 0
                && !IsOwnOrigin(ctx.Request.Headers.Origin.ToString(), ctx.Connection.LocalPort))
            {
                await Error(403, "forbidden_origin", "cross-origin requests are not allowed").ExecuteAsync(ctx);
                return;
            }

            await next(ctx);
        });

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

    /// <summary>True for <c>http://&lt;loopback name&gt;:&lt;this server's port&gt;</c>; <c>null</c> and lists are rejected.</summary>
    public static bool IsOwnOrigin(string origin, int port) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.Port == port
        && uri.AbsolutePath == "/"
        && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

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
        ["last_poll"] = new JsonObject
        {
            ["at"] = s.LastPoll.At?.ToString("O"),
            ["error"] = s.LastPoll.Error,
            ["active_states"] = new JsonArray(s.LastPoll.ActiveStates.Select(x => (JsonNode)x).ToArray()),
        },
        ["queue"] = new JsonArray(s.Queue.Select(c => (JsonNode)new JsonObject
        {
            ["issue_id"] = c.IssueId,
            ["issue_identifier"] = c.IssueIdentifier,
            ["issue_url"] = c.IssueUrl,
            ["title"] = c.Title,
            ["state"] = c.State,
            ["priority"] = c.Priority,
            ["status"] = c.Status,
            ["reason"] = c.Reason,
        }).ToArray()),
        ["handoffs"] = new JsonArray(s.Handoffs.Select(h => (JsonNode)new JsonObject
        {
            ["issue_id"] = h.IssueId,
            ["issue_identifier"] = h.IssueIdentifier,
            ["issue_url"] = h.IssueUrl,
            ["title"] = h.Title,
            ["state"] = h.State,
            ["at"] = h.At.ToString("O"),
            ["reason"] = h.Reason,
        }).ToArray()),
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
