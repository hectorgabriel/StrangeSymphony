using Symphony.Core.Config;
using Symphony.Core.Domain;

namespace Symphony.Core.Tracker;

/// <summary>
/// Startup check against the tracker's own workflows: a state an agent is meant to reach (a gate's state, a
/// send-back state, or one of <c>agent.allowed_states</c>) that a work item type in the queue doesn't have can
/// never be reached for items of that type, and the agent only finds out from a rejected <c>update_state</c>.
/// </summary>
public static class WorkflowStateCheck
{
    public static IReadOnlyList<string> Warnings(ServiceConfig config, IReadOnlyList<WorkItemTypeStates> types)
    {
        var targets = new List<(string State, string Why)>();
        foreach (var gate in config.Gates?.Values ?? [])
        {
            targets.Add((gate.State, $"the gate on \"{gate.State}\""));
        }

        foreach (var state in config.Defects?.States ?? [])
        {
            targets.Add((state, $"the send-back state \"{state}\" (defects.states)"));
        }

        foreach (var state in config.Agent.AllowedStates ?? [])
        {
            if (!config.IsTerminalState(state))
            {
                targets.Add((state, $"\"{state}\" in agent.allowed_states"));
            }
        }

        var warnings = new List<string>();
        foreach (var type in types)
        {
            var have = type.States.Select(StateNames.Normalize).ToHashSet();
            foreach (var group in targets.GroupBy(t => StateNames.Normalize(t.State)).Where(g => !have.Contains(g.Key)))
            {
                warnings.Add($"work item type \"{type.Type}\" has no state \"{group.First().State}\" (its states: {string.Join(", ", type.States)}),"
                    + $" so {string.Join(" and ", group.Select(t => t.Why).Distinct())} can't be reached for it;"
                    + " leave the type out of the queue or use a state it has");
            }
        }

        return warnings;
    }
}
