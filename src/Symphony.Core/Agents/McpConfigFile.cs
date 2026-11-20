using System.Text;
using System.Text.Json.Nodes;

namespace Symphony.Core.Agents;

/// <summary>
/// The private MCP config file that points an agent CLI at its session's tool endpoint. It holds the endpoint's
/// bearer token, so it is created owner-only under Symphony's state directory and deleted with the session.
/// </summary>
public static class McpConfigFile
{
    /// <summary>The MCP server name agents see the host's tools under.</summary>
    public const string TrackerServerName = "tracker";

    /// <summary>Writes <c>{"mcpServers":{"tracker":{"type":"http",...}}}</c> and returns its path.</summary>
    public static string Write(string privateStateDir, ToolEndpoint endpoint)
    {
        var dir = Path.Combine(privateStateDir, "mcp");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.json");
        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [TrackerServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = endpoint.Url,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {endpoint.BearerToken}" },
                },
            },
        };

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json.ToJsonString());
        }

        return path;
    }
}
