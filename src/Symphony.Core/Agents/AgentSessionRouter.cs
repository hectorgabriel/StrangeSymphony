namespace Symphony.Core.Agents;

/// <summary>Starts each session with the factory of its route's runner (<c>agent.runner</c>).</summary>
public sealed class AgentSessionRouter(IReadOnlyDictionary<string, IAgentSessionFactory> factories) : IAgentSessionFactory
{
    public Task<IAgentSession> StartAsync(AgentSessionRequest request, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var runner = request.EffectiveRoute.Runner;
        return factories.TryGetValue(runner, out var factory)
            ? factory.StartAsync(request with { Route = request.EffectiveRoute }, onEvent, ct)
            : throw new AgentException(AgentErrorCodes.AgentNotFound, $"no agent runner '{runner}' is available in this build");
    }
}
