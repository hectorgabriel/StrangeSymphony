using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Cli;

namespace Symphony.Tests;

public class StatusServerTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("LOCALHOST", HttpStatusCode.OK)]
    [InlineData("[::1]", HttpStatusCode.OK)]
    [InlineData("evil.example", HttpStatusCode.BadRequest)]
    [InlineData("127.0.0.1.evil.example", HttpStatusCode.BadRequest)]
    public async Task Only_loopback_host_headers_are_served(string? host, HttpStatusCode expected)
    {
        await using var h = new OrchestratorHarness();
        h.Start();
        await Wait.Until(() => TrySnap(h), "first publish");
        await using var server = new StatusServer(h.Orchestrator, NullLogger<StatusServer>.Instance);
        await server.StartAsync(0, default);
        var url = new Uri(server.Url!);

        using var http = new HttpClient();
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/state"), (HttpMethod.Get, "/"), (HttpMethod.Get, "/score"), (HttpMethod.Post, "/api/v1/refresh") })
        {
            using var req = new HttpRequestMessage(method, new Uri(url, path));
            if (host is not null) req.Headers.Host = $"{host}:{url.Port}";
            using var res = await http.SendAsync(req);
            var ok = expected == HttpStatusCode.OK && res.IsSuccessStatusCode;
            Assert.True(ok || res.StatusCode == expected, $"{method} {path} with Host {host}: {(int)res.StatusCode}");
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Accepted)]
    [InlineData("http://127.0.0.1:{PORT}", HttpStatusCode.Accepted)]
    [InlineData("http://localhost:{PORT}", HttpStatusCode.Accepted)]
    [InlineData("http://[::1]:{PORT}", HttpStatusCode.Accepted)]
    [InlineData("https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("http://127.0.0.1.evil.example:{PORT}", HttpStatusCode.Forbidden)]
    [InlineData("http://localhost:1", HttpStatusCode.Forbidden)]
    [InlineData("https://127.0.0.1:{PORT}", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    [InlineData("http://127.0.0.1:{PORT}, https://evil.example", HttpStatusCode.Forbidden)]
    public async Task Refresh_rejects_cross_origin_posts(string? origin, HttpStatusCode expected)
    {
        await using var h = new OrchestratorHarness();
        h.Start();
        await Wait.Until(() => TrySnap(h), "first publish");
        await using var server = new StatusServer(h.Orchestrator, NullLogger<StatusServer>.Instance);
        await server.StartAsync(0, default);
        var url = new Uri(server.Url!);

        using var http = new HttpClient();
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "/api/v1/refresh"));
        if (origin is not null) post.Headers.TryAddWithoutValidation("Origin", origin.Replace("{PORT}", url.Port.ToString(CultureInfo.InvariantCulture)));
        using var res = await http.SendAsync(post);
        Assert.Equal(expected, res.StatusCode);

        // Reads are unaffected: browsers already block cross-origin reads without CORS headers.
        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(url, "/api/v1/state"));
        get.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var state = await http.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.False(state.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void State_json_includes_the_queue_last_poll_and_handoffs()
    {
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var snapshot = new Symphony.Core.Orchestration.RuntimeSnapshot(
            at, [], [], new(0, 0, 0, 0), null, null, new Dictionary<string, Symphony.Core.Orchestration.IssueDetail>(),
            [new("id-1", "WR-1", "https://x.test/1", "Do it", "Active", 2, "blocked", "Blocked by WR-0 (Analizado)")],
            new(at, null, ["Active", "En Proceso"]),
            [new("id-0", "WR-0", null, "Done it", "Analizado", at, "Moved to \"Analizado\"")]);

        var json = StatusServer.StateJson(snapshot);

        var row = json["queue"]![0]!;
        Assert.Equal("WR-1", (string?)row["issue_identifier"]);
        Assert.Equal("blocked", (string?)row["status"]);
        Assert.Equal("Blocked by WR-0 (Analizado)", (string?)row["reason"]);
        Assert.Equal(2, (int?)row["priority"]);
        Assert.Equal(at.ToString("O"), (string?)json["last_poll"]!["at"]);
        Assert.Null(json["last_poll"]!["error"]);
        Assert.Equal("En Proceso", (string?)json["last_poll"]!["active_states"]![1]);
        var handoff = json["handoffs"]![0]!;
        Assert.Equal(("WR-0", "Analizado", "Moved to \"Analizado\""), ((string?)handoff["issue_identifier"], (string?)handoff["state"], (string?)handoff["reason"]));
    }

    private static bool TrySnap(OrchestratorHarness h)
    {
        try
        {
            _ = h.Snap;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
