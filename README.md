# Symphony (.NET · Azure DevOps · Wrike · Claude Code)

A .NET 10 implementation of the [Symphony service spec](SPEC.md). It watches a tracker (Azure Boards or Wrike) for work items, gives each eligible item its own workspace, and runs a headless **Claude Code** session on it until the item reaches its handoff state.

```
Azure Boards ──poll──▶ Orchestrator ──dispatch──▶ Worker ──▶ claude -p (stream-json), cwd = <root>/WI-123
     ▲                    │  retries, reconcile,        │
     └──── tools (MCP, host-side PAT) ◀─────────────────┘
```

## Quick start

1. **Prerequisites:** the .NET 10 SDK and the Claude Code CLI (`claude`).
2. **Sign in to Claude Code** on this machine with your own Pro, Max or Team account (run `claude` once and log in). Alternatively, set `ANTHROPIC_API_KEY`.
3. **Create a tracker token:**
   - Azure Boards: a PAT with the *Work Items: Read & Write* scope, exported as `AZURE_DEVOPS_PAT`.
   - Wrike: a permanent access token (Apps & Integrations → API), exported as `WRIKE_TOKEN`.
   ```bash
   export AZURE_DEVOPS_PAT=...   # or: export WRIKE_TOKEN=...
   ```
4. **Add a workflow file:** copy [`WORKFLOW.example.md`](WORKFLOW.example.md) to `WORKFLOW.md` in the repository the agent should work on, and edit the organization, project, states and hooks.
5. **Run it:**
   ```bash
   dotnet run --project src/Symphony.Cli -- path/to/WORKFLOW.md --port 4510
   ```
   Then open <http://127.0.0.1:4510> for the dashboard. Its read-only board shows each task in an active state and why it is or isn't running, plus the tasks Symphony recently handed off. The API is at `/api/v1/state`, `/api/v1/<WI-123>` and `POST /api/v1/refresh`.

To have the agent pick up an item in Azure Boards, give it an active state, assign it to yourself (with `assigned_to: "@me"`), and add the `agent` tag (with `required_labels: [agent]`). In Wrike, make the task a subtask of the queue task (`parent_task_id`) and give it an active status.

## Windows

Symphony runs on Windows too. The same `WORKFLOW.md` works there because hooks run in Git Bash.

- **Prerequisites:** [Git for Windows](https://git-scm.com/download/win) (it provides Git Bash, which Claude Code also needs for its Bash tool), the .NET 10 SDK, and `claude`.
- **Tokens:** `setx WRIKE_TOKEN "..."` (or `AZURE_DEVOPS_PAT`), then open a new terminal; or `$env:WRIKE_TOKEN = "..."` for the current PowerShell session only.
- **Hook shell:** `hooks.shell: auto` (the default) finds Git Bash by path, never the WSL `bash.exe` in `System32`. Set `CLAUDE_CODE_GIT_BASH_PATH` if Git lives somewhere unusual, or `hooks.shell: powershell` to write hooks in PowerShell.
- **Paths:** keep `workspace.root` short (for example `'C:/sym/ws'`) and run `git config --global core.longpaths true`; deep repositories pass the 260-character limit quickly. In YAML, use forward slashes or single quotes for Windows paths.
- **Git credentials:** clone the repository once by hand so Git Credential Manager stores the login. Hooks run unattended and can't show a sign-in prompt.

Details are in [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md#other-implementation-defined-choices).

## Team setup: everyone uses their own login

Each team member runs their own copy against the same `WORKFLOW.md` in the repo:

- `assigned_to: "@me"`: each copy only dispatches items assigned to the owner of its PAT. Assignment in Azure Boards decides whose agent works on what.
- Each member's own `claude` login is used, so usage counts against their own plan.
- Each member's own PAT is used, so comments and state changes show their name.
- Keep `max_concurrent_agents` low (1–2) on Pro plans.

See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md#authentication-and-accounts) for what is and isn't allowed with subscription logins.

## Layout

| Project | Contents |
|---|---|
| `src/Symphony.Core` | Workflow loader and hot reload, typed config, strict prompt rendering, workspace manager and hooks, orchestrator, worker |
| `src/Symphony.AzureDevOps` | Azure Boards adapter: WIQL, batch fetch, normalization, `dispatchable`, tools ([profile](docs/adapters/azure-devops.md)) |
| `src/Symphony.Wrike` | Wrike adapter: queue subtasks, custom status mapping, dependencies, tools ([profile](docs/adapters/wrike.md)) |
| `src/Symphony.Claude` | Claude Code stream-json session runner |
| `src/Symphony.Cli` | `symphony` executable: host, loopback MCP tool gateway, dashboard and JSON API |
| `tests/Symphony.Tests` | Conformance tests (§17) |

```bash
dotnet test                                                 # core + extension conformance
SYMPHONY_REAL_CLAUDE=1 dotnet test --filter RealIntegration # real claude CLI smoke test
```

## Docs

- [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md): how the Codex protocol maps to Claude Code, the trust and safety posture, and implementation-defined behavior
- [docs/adapters/azure-devops.md](docs/adapters/azure-devops.md): the Azure DevOps adapter profile (config keys, mapping, tools, errors)
- [docs/adapters/wrike.md](docs/adapters/wrike.md): the Wrike adapter profile
- [docs/sdlc-pipeline.md](docs/sdlc-pipeline.md): one Wrike queue through analysis, development, testing and documentation, with the role agents and BossDream gates
- [docs/pull-requests.md](docs/pull-requests.md): the `create_pull_request` tool, which opens a pull request only after the host has checked the branch and built the pushed commit
