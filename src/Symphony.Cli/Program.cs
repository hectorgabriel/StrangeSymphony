using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Symphony.AzureDevOps;
using Symphony.Claude;
using Symphony.Cli;
using Symphony.Core.Config;
using Symphony.Core.Orchestration;
using Symphony.Core.Workflow;
using Symphony.Core.Workspaces;
using Symphony.Wrike;

return await SymphonyCli.RunAsync(args);

namespace Symphony.Cli
{
    public static class SymphonyCli
    {
        public const string Usage = """
            Usage: symphony [path-to-WORKFLOW.md] [--port <n>] [--log-level <level>]

              path-to-WORKFLOW.md   Workflow file (default: ./WORKFLOW.md)
              --port <n>            Enable the HTTP dashboard/API on 127.0.0.1:<n> (overrides server.port; 0 = ephemeral)
              --log-level <level>   Trace|Debug|Information|Warning|Error (default: Information)
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            string? workflowArg = null;
            int? cliPort = null;
            var logLevel = LogLevel.Information;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h" or "--help":
                        Console.WriteLine(Usage);
                        return 0;
                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p >= 0:
                        cliPort = p;
                        i++;
                        break;
                    case "--log-level" when i + 1 < args.Length && Enum.TryParse<LogLevel>(args[i + 1], true, out var level):
                        logLevel = level;
                        i++;
                        break;
                    case var a when !a.StartsWith('-') && workflowArg is null:
                        workflowArg = a;
                        break;
                    default:
                        Console.Error.WriteLine($"symphony: invalid argument '{args[i]}'\n\n{Usage}");
                        return 2;
                }
            }

            using var loggerFactory = LoggerFactory.Create(b => b
                .SetMinimumLevel(logLevel)
                .AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.UseUtcTimestamp = true;
                    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
                    o.IncludeScopes = false;
                }));
            var log = loggerFactory.CreateLogger("Symphony");

            var workflowPath = Path.GetFullPath(workflowArg ?? "WORKFLOW.md");
            if (!File.Exists(workflowPath))
            {
                log.LogError("action=startup outcome=failed code=missing_workflow_file path={Path}", workflowPath);
                return 1;
            }

            Func<string, string?> env = Environment.GetEnvironmentVariable;
            var builder = new ConfigBuilder([new AzureDevOpsAdapterFactory(), new WrikeAdapterFactory()], env);
            using var store = new WorkflowStore(workflowPath, builder, env, loggerFactory);

            EffectiveWorkflow initial;
            try
            {
                initial = store.LoadInitial();
            }
            catch (WorkflowException ex)
            {
                log.LogError("action=startup outcome=failed code={Code} reason={Reason}", ex.Code, ex.Message);
                return 1;
            }

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                stop.Cancel();
            });

            var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "symphony");
            await using var gateway = new McpToolGateway(loggerFactory.CreateLogger<McpToolGateway>());
            StatusServer? status = null;
            try
            {
                await gateway.StartAsync(loggerFactory, stop.Token);

                var workspaces = new WorkspaceManager(loggerFactory.CreateLogger<WorkspaceManager>());
                var agents = new ClaudeCodeSessionFactory(gateway, stateDir, loggerFactory);
                var worker = new AgentWorker(workspaces, agents, loggerFactory.CreateLogger<AgentWorker>());
                var orchestrator = new Orchestrator(store, workspaces, worker.RunAsync, TimeProvider.System, loggerFactory.CreateLogger<Orchestrator>());

                // CLI --port overrides server.port; listener changes need a restart (SPEC 13.7).
                if ((cliPort ?? initial.Config.ServerPort) is { } port)
                {
                    status = new StatusServer(orchestrator, loggerFactory.CreateLogger<StatusServer>());
                    await status.StartAsync(port, stop.Token);
                }

                store.StartWatching();
                log.LogInformation("action=startup outcome=completed workflow={Path} tracker={Tracker} workspace_root={Root} max_concurrent_agents={Max}",
                    workflowPath, initial.Config.Tracker.Kind, initial.Config.WorkspaceRoot, initial.Config.Agent.MaxConcurrentAgents);

                await orchestrator.RunAsync(stop.Token);
                return 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception ex)
            {
                log.LogCritical(ex, "action=host outcome=crashed reason={Reason}", ex.Message);
                return 1;
            }
            finally
            {
                if (status is not null) await status.DisposeAsync();
            }
        }
    }
}
