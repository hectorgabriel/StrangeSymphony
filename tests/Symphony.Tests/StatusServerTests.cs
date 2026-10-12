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
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/state"), (HttpMethod.Get, "/"), (HttpMethod.Post, "/api/v1/refresh") })
        {
            using var req = new HttpRequestMessage(method, new Uri(url, path));
            if (host is not null) req.Headers.Host = $"{host}:{url.Port}";
            using var res = await http.SendAsync(req);
            var ok = expected == HttpStatusCode.OK && res.IsSuccessStatusCode;
            Assert.True(ok || res.StatusCode == expected, $"{method} {path} with Host {host}: {(int)res.StatusCode}");
        }
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
