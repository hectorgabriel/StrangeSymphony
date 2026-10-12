using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Cli;

namespace Symphony.Tests;

public class McpToolGatewayTests
{
    private static async Task<(JsonNode Result, (LogLevel Level, string Message) Log)> Call(string tool)
    {
        var logger = new CapturingLogger<McpToolGateway>();
        await using var gateway = new McpToolGateway(logger);
        await gateway.StartAsync(NullLoggerFactory.Instance, default);
        var endpoint = gateway.Register(new FakeTracker(), () => TestIssues.Make("1"), "WI-1");

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool } }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.BearerToken);
        using var res = await http.SendAsync(req);
        var result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["result"]!;
        return (result, Assert.Single(logger.Snapshot(), e => e.Message.Contains("action=tool_call", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Failed_tool_result_is_logged_with_category_and_reason()
    {
        var (result, log) = await Call("reject");
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("tool=reject outcome=failed", log.Message, StringComparison.Ordinal);
        Assert.Contains("category=tracker_status", log.Message, StringComparison.Ordinal);
        Assert.Contains("reason=transition New -> Done is not allowed", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_tool_is_logged_as_tool_error()
    {
        var (result, log) = await Call("explode");
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("category=tool_error reason=adapter blew up", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_tool_is_logged_with_its_category()
    {
        var (_, log) = await Call("nope");
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("outcome=unsupported", log.Message, StringComparison.Ordinal);
        Assert.Contains("category=unsupported_tool", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_tool_is_logged_as_completed()
    {
        var (result, log) = await Call("echo");
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Contains("tool=echo outcome=completed", log.Message, StringComparison.Ordinal);
    }
}
