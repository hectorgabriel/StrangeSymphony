# Verification gates (`gates`, extension)

Without gates, Symphony accepts the agent's word: when the agent calls `update_state`, the status changes. A gate makes the host check the work first. You name a target status, and before any agent may move an issue there, Symphony checks in the workspace that the work is committed and pushed and runs your commands. The status changes only if every check passes.

```yaml
gates:
  Por Probar:                         # the status the agent asks for (case-insensitive)
    run:
      - dotnet build --nologo
      - dotnet test --nologo --no-build
    timeout_ms: 600000                # all commands together; default 900000
  En Revision:
    run: dotnet test --nologo         # one command can be a plain string
  Analizado: {}                       # no commands: only "committed and pushed"
```

| Key | Default | Meaning |
|---|---|---|
| `run` | `[]` | Commands, run in order in the workspace with `hooks.shell`. The first one that fails, or exits non-zero, stops the gate. |
| `require_pushed` | `true` | Before the commands run: `HEAD` is on a branch, the workspace has no uncommitted changes (untracked files count), and `<remote>/<branch>` is exactly `HEAD`. |
| `remote` | `origin` | The remote that `require_pushed` compares with. |
| `timeout_ms` | `900000` | Time for all of the gate's commands together. |

The section is validated when the workflow loads. An unknown key, an empty command, or a gate that checks nothing (`require_pushed: false` with no `run`) stops startup or a reload. A misspelt gate must not let work through unchecked.

## What happens on a status change

1. The agent calls `update_state` with `state: "Por Probar"`.
2. Symphony finds the gate for that status and runs it in the issue's workspace:
   - `require_pushed` checks, in order: `not_on_branch`, `uncommitted_changes`, `not_pushed`.
   - Each `run` command: `verification_failed`, which ends with the last 8 KB of the command's output, where build and test errors usually are.
   - After the commands, the tracked files and `HEAD` must be unchanged, because the check has to cover what was pushed: `workspace_changed`.
3. **If it fails**, the tracker is not called. The agent gets the category and a message starting *Symphony's verification gate for "Por Probar" did not pass, so the status was not changed*, followed by the reason. The issue stays active in its current status, so the agent keeps working in the same session: it fixes the problem, pushes and calls `update_state` again. If it runs out of turns, the normal continuation and retry rules apply.
4. **If it passes**, the status changes as usual. The tool result gets a `gate` object (`state`, `commit`, `branch`, `commands`, `seconds`).

The `update_state` tool description lists the gated statuses, so the agent knows in advance that the move is checked. Each run, failure and pass also appears as a notification on the dashboard and in the log (`action=gate outcome=running|passed|failed`).

## What gates guarantee and what they don't

- **The host runs the checks.** The agent can't skip a gate, run it against another commit, or report a test run that never happened. `update_state` is the only tool either tracker adapter offers for changing a status, so every agent move goes through the gate.
- **People aren't gated.** A status changed by hand in Wrike or Azure Boards doesn't go through the gate. Gates check agents, and your review gates stay with people.
- **Commands run the project's code**, including tests the agent wrote, so they see neither the tracker's credentials nor the pull request token. Both are removed from the commands' environment, as they are from the agent's. Other variables from Symphony's environment are still passed. Don't start Symphony with database connection strings in its environment.
- **The gate checks what the commands check.** `dotnet test` passing proves the tests pass, not that they test the right thing. That's still the tester's and the reviewer's job.

## Timeouts

A tool call produces no agent events, so to the orchestrator a long gate looks like a quiet session. Symphony posts a notification when the gate starts, which resets the stall timer. Keep `timeout_ms` plus two minutes for the git checks below `claude.stall_timeout_ms`; if it isn't, the workflow loads with a warning. The `update_state` call's own limit is raised to the longest gate plus those two minutes.

## Gates and BossDream

BossDream's Stop hook runs the quality pipeline inside the agent's session, from hooks in the target repository. It is useful but runs on the agent's side: the agent works in that repository, so it can change those hooks. A Symphony gate runs outside the agent's reach and checks the pushed commit. Keep both. To avoid building twice, keep the gate light: `dotnet build` plus `dotnet test --no-build`, without Stryker or other slow tiers.

## Suggested gates for the SDLC pipeline

| Target status | Who moves there | Gate |
|---|---|---|
| Analizado | analyst | `{}`: the documents are committed and pushed |
| Plan Propuesto | developer (plan) | `{}`: `plans/<id>/tasks.md` is pushed |
| Por Probar | developer | build and tests |
| En Revision | tester | build and tests, including the tester's new tests |
| Con Defectos | tester | `{}`: the test cases are pushed. No test run, because failing tests are a valid reason to be here |
| Documentado | documentador | `{}`: the manual is pushed |
| Requiere Informacion | any | none: an agent that's blocked must always be able to stop |

[`WORKFLOW.sdlc.example.md`](../WORKFLOW.sdlc.example.md) has this configuration.
