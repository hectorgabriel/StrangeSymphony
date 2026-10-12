using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Symphony.Core.Agents;
using Symphony.Core.Domain;
using Symphony.Core.Tracker;

namespace Symphony.Cli;

/// <summary>
/// Loopback MCP (Streamable HTTP, JSON responses) server that exposes the tracker adapter's tools
/// to agent sessions. Tool calls execute in this process with the adapter's credentials; the agent
/// only holds a per-session bearer token that is useless outside this endpoint (SPEC 10.5, 15.3).
/// </summary>
public sealed class McpToolGateway : IToolGateway, IAsyncDisposable
{
    private const string ServerName = "symphony-tracker";

    private sealed record Registration(ITrackerAdapter Tracker, Func<Issue> CurrentIssue, string Identifier, byte[] TokenHash);

    private readonly ConcurrentDictionary<string, Registration> _sessions = new();
    private readonly ILogger<McpToolGateway> _logger;
    private WebApplication? _app;
    private string? _baseUrl;

    public McpToolGateway(ILogger<McpToolGateway> logger) => _logger = logger;

    public async Task StartAsync(ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapPost("/mcp/{key}", HandleAsync);
        _app.MapGet("/mcp/{key}", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        _app.MapDelete("/mcp/{key}", () => Results.Ok());
        await _app.StartAsync(ct);

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _baseUrl = address.TrimEnd('/');
        _logger.LogInformation("action=tool_gateway outcome=started url={Url}", _baseUrl);
    }

    public ToolEndpoint Register(ITrackerAdapter tracker, Func<Issue> currentIssue, string issueIdentifier)
    {
        var baseUrl = _baseUrl ?? throw new InvalidOperationException("tool gateway not started");
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[key] = new Registration(tracker, currentIssue, issueIdentifier, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return new ToolEndpoint($"{baseUrl}/mcp/{key}", token, new Unregister(() => _sessions.TryRemove(key, out _)));
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private async Task<IResult> HandleAsync(string key, HttpContext http)
    {
        if (!_sessions.TryGetValue(key, out var reg) || !Authorized(http, reg))
        {
            return Results.Json(new { error = new { code = "unauthorized", message = "unknown or unauthorized tool session" } }, statusCode: 401);
        }

        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.Json(RpcError(null, -32700, "parse error"), statusCode: 400);
        }

        if (body is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch.OfType<JsonObject>())
            {
                if (await DispatchAsync(item, reg, http.RequestAborted) is { } r) responses.Add(r);
            }

            return responses.Count == 0 ? Results.StatusCode(202) : Json(responses);
        }

        if (body is not JsonObject request)
        {
            return Results.Json(RpcError(null, -32600, "invalid request"), statusCode: 400);
        }

        var response = await DispatchAsync(request, reg, http.RequestAborted);
        return response is null ? Results.StatusCode(202) : Json(response);
    }

    private static IResult Json(JsonNode node) => Results.Text(node.ToJsonString(), "application/json", Encoding.UTF8);

    private static bool Authorized(HttpContext http, Registration reg)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(presented, reg.TokenHash);
    }

    private async Task<JsonObject?> DispatchAsync(JsonObject request, Registration reg, CancellationToken ct)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (id is null)
        {
            return null; // notification
        }

        switch (method)
        {
            case "initialize":
            {
                var version = request["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
                return RpcResult(id, new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                    ["instructions"] = $"Tracker tools for work item {reg.Identifier}. Every tool acts on that work item only.",
                });
            }

            case "ping":
                return RpcResult(id, new JsonObject());

            case "tools/list":
                return RpcResult(id, new JsonObject
                {
                    ["tools"] = new JsonArray(reg.Tracker.AgentToolSpecs().Select(s => (JsonNode)new JsonObject
                    {
                        ["name"] = s.Name,
                        ["description"] = s.Description,
                        ["inputSchema"] = s.InputSchema.DeepClone(),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = !s.Mutates },
                    }).ToArray()),
                });

            case "tools/call":
            {
                var name = request["params"]?["name"]?.GetValue<string>() ?? "";
                var args = request["params"]?["arguments"] as JsonObject ?? new JsonObject();
                var result = await CallToolAsync(reg, name, args, ct);
                var text = result.Output?.ToJsonString() ?? "null";
                var payload = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = !result.Success,
                };
                if (result.Output is JsonObject structured)
                {
                    payload["structuredContent"] = structured.DeepClone();
                }

                return RpcResult(id, payload);
            }

            default:
                return RpcError(id, -32601, $"method not found: {method}");
        }
    }

    private async Task<AgentToolResult> CallToolAsync(Registration reg, string name, JsonObject args, CancellationToken ct)
    {
        var issue = reg.CurrentIssue();
        if (!reg.Tracker.AgentToolSpecs().Any(s => s.Name == name))
        {
            _logger.LogWarning("action=tool_call tool={Tool} outcome=unsupported issue_id={IssueId} issue_identifier={IssueIdentifier}", name, issue.Id, issue.Identifier);
            return AgentToolResult.Fail("unsupported_tool", $"unsupported tool: {name}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await reg.Tracker.ExecuteAgentToolAsync(name, args, issue, timeout.Token);
            _logger.LogInformation("action=tool_call tool={Tool} outcome={Outcome} issue_id={IssueId} issue_identifier={IssueIdentifier}",
                name, result.Success ? "completed" : "failed", issue.Id, issue.Identifier);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("action=tool_call tool={Tool} outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} reason={Reason}",
                name, issue.Id, issue.Identifier, ex.Message);
            return AgentToolResult.Fail(ex is OperationCanceledException ? "timeout" : "tool_error", ex.Message);
        }
    }

    private static JsonObject RpcResult(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
