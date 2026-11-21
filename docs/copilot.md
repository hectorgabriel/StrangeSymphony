# GitHub Copilot CLI as the coding agent

`agent.runner: copilot` runs each session with [GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically) instead of Claude Code. Everything around the agent stays the same: the tracker, workspaces, hooks, tracker tools, gates, defects, budgets, the audit trail and the dashboard.

```
Orchestrator ──▶ Worker ──▶ copilot --output-format json --session-id=<id>   (turn 1, prompt on stdin)
                        └─▶ copilot --output-format json --resume=<id>       (turn 2, 3, …)
```

Checked against Copilot CLI 1.0.114.

## 1. Set up

1. Install Copilot CLI (`brew install copilot-cli`, `npm install -g @github/copilot` or WinGet) and sign in once with `copilot login`, using your own Copilot seat.
2. On a Business or Enterprise seat, your organization must allow Copilot CLI, and its MCP policy must allow local servers. Symphony gives the agent its tracker tools through a local MCP server; without it the agent can't move tasks, and the run fails (see below).
3. Switch the workflow to Copilot:

```yaml
agent:
  runner: copilot

copilot:
  command: copilot
  model: claude-sonnet-5.5          # or gpt-6-sol, auto, …; omit for Copilot's default
  model_by_state:
    Por Analizar: claude-opus-5.5
  allow_tools: [write, "shell(git:*)", "shell(dotnet:*)"]
  deny_tools: ["shell(git push --force)"]
  stall_timeout_ms: 1200000
```

The `claude` section stays as it is and is ignored while no status runs on Claude. To run only some statuses on Copilot, see section 9.

## 2. Settings (`copilot`)

| Key | Default | Meaning |
|---|---|---|
| `command` | `copilot` | The CLI, launched like `claude.command` (a login shell, or the `.exe` directly on Windows). |
| `model` | Copilot's default | `--model`. Use the names `copilot help` and `/model` show. |
| `model_by_state` | none | A model per status, as in [hybrid-models.md](hybrid-models.md). A session ends when the task moves to a status with a different model. |
| `reasoning_effort` | the model's | `none`, `minimal`, `low`, `medium`, `high`, `xhigh` or `max`. |
| `allow_tools` | `[write]` | `--allow-tool` rules: `write`, `shell(git:*)`, `shell(dotnet test)`, `url(https://*.example.com)`. Everything else that would ask for permission is denied, as Claude's `acceptEdits` does. Read-only commands run without a rule. |
| `deny_tools` | none | `--deny-tool` rules. They win over every allow. |
| `excluded_tools` | none | `--excluded-tools`: tools the model never sees. |
| `home` | Symphony's state directory | `COPILOT_HOME` for agent sessions (see section 4). |
| `trust_workspace_root` | `true` | Adds `workspace.root` to the home's `trustedFolders`, so repository hooks and `.mcp.json` run. |
| `extra_args` | none | Appended to every run. |
| `turn_timeout_ms` | `3600000` | A run that prints nothing for this long is stopped (`turn_timeout`). |
| `stall_timeout_ms` | `300000` | The orchestrator's stall detection; `<= 0` turns it off. Gate and PR warnings use it. |
| `resume` | `true` | Continue the task's last session in the same status, runner and model. |

How Claude's permission settings translate:

| `claude` | `copilot` |
|---|---|
| `permission_mode: acceptEdits` | `allow_tools: [write]` |
| `allowed_tools: ["Bash(git *)"]` | `allow_tools: ["shell(git:*)"]` |
| `disallowed_tools: [WebFetch]` | `deny_tools: ["url"]` or `excluded_tools: [web_fetch]` |
| `disallowed_tools: [AskUserQuestion]` | always on (`--no-ask-user`) |

`shell(gh pr)` style rules match a command and its first subcommand only, so `shell(gh pr)` also allows `gh pr merge`.

## 3. How a session runs

- **One process per turn.** The first turn creates the session with `--session-id=<uuid>` (Symphony picks the id); later turns, and later runs that resume, use `--resume=<id>`. The prompt goes on stdin, so its length doesn't matter on Windows either.
- **Unattended flags on every run:** `--output-format json`, `--no-ask-user`, `--sync-session=off` (session history stays on this machine), `--disable-builtin-mcps` (no GitHub MCP server).
- **Tracker tools:** a private, owner-only MCP config (`type: http`, the session's bearer token) is passed with `--additional-mcp-config=@<file>`, plus `--allow-tool=tracker`. Copilot names the tools `tracker-<tool>`. If the tracker server isn't `connected` when the session loads (a policy block, a bad token), Copilot would carry on without it and exit 0, so Symphony fails the turn instead: `the tracker MCP server did not connect (status needs-auth)`.
- **Events:** `tool.execution_start` becomes `tool_call`, assistant text and warnings become `notification`, failed tool calls (including permission denials) are reported, `session.error` fails the turn, and the final `result` line ends it.
- **Tokens:** read from `--usage-output-file` after each run. Input counts cache reads and writes, as for Claude. The file counts the whole session, so Symphony keeps the last total per session under its state directory and reports only what this run used.
- **Resume fallback:** if the saved session is gone (`No session, task, or name matched`), the turn fails as `port_exit` and the worker starts a new session with the task prompt, as with Claude.

## 4. The agent home and trust

Agent sessions run with their own `COPILOT_HOME`, by default `copilot-home` under Symphony's state directory (`~/.local/share/symphony` on Linux, `~/Library/Application Support/symphony` on macOS, `%LOCALAPPDATA%\symphony` on Windows). Your own `~/.copilot` (its MCP servers, plugins, settings and history) doesn't leak into agent sessions, and agent sessions don't fill your history.

- **Login:** on macOS the login is kept in the keychain, so the separate home needs no second login. If a run fails because it isn't signed in, run `COPILOT_HOME=<that directory> copilot login` once, or set `copilot.home: ~/.copilot` to share your normal home.
- **Trust:** Copilot skips repository hooks and `.mcp.json` in folders it doesn't trust, and its `--trust-folder` flag can't be combined with `--session-id`. Symphony adds `workspace.root` (and its real path, if a symlink is on the way) to `trustedFolders` in the home's `config.json`; subfolders are trusted with it. Turn this off with `trust_workspace_root: false`.
- **Clean up:** session history is stored unencrypted under `<home>/session-state/` and never expires. Delete old sessions there when you no longer need them.

## 5. Environment and secrets

On top of the tracker's credentials and `workspace.strip_env` (see [data-isolation.md](data-isolation.md)), Symphony removes `GH_TOKEN` and `GITHUB_TOKEN` (Copilot would sign in with them before its own login, and a classic `ghp_` token fails), `COPILOT_ALLOW_ALL`, `CLAUDECODE` and the variables an outer Copilot session sets. It sets:

- `COPILOT_DISABLE_LOGIN_SHELL_ENV=1`: otherwise Copilot reads `bash -l` once and copies its exports into the agent's shell, which would bring back variables Symphony removed.
- `COPILOT_DISABLE_RESUME_AUTO_CD=1`: otherwise `--resume` changes to the directory the session was recorded in instead of the workspace.
- `COPILOT_AUTO_UPDATE=false`.

`COPILOT_GITHUB_TOKEN` is kept, as the way to sign Copilot in with a fine-grained token that has the "Copilot Requests" permission, and passed in `--secret-env-vars` so the agent's shell doesn't see it.

## 6. The SDLC pipeline and BossDream

Copilot reads `CLAUDE.md`, `.claude/agents`, `.claude/skills` and `.claude/commands`, and runs hooks from `.claude/settings.json` under Claude's event names (`SessionStart`, `PreToolUse`, `Stop`) with Claude's field names. One difference matters for gates: **a `Stop` hook blocks only with JSON output** (`{"decision":"block","reason":"…"}`). Exit code 2, which blocks in Claude Code, is only a warning in Copilot. Check how the repository's BossDream hooks block before running the pipeline on Copilot. Copilot also stops honoring a Stop hook after 8 blocks in a row, and gives each hook 30 seconds by default.

## 7. Cost

Copilot bills in AI credits, priced per token, from your plan. Every model call resends the conversation, and a trivial run costs about 2 to 6.5 credits (17,000 to 24,000 tokens of system prompt and tools). `budget.max_tokens_per_issue` and `budget.max_tokens_per_day` count Copilot sessions the same way as Claude sessions. To cap a single run's credits as well, add `extra_args: ["--max-ai-credits=50"]` (minimum 30).

## 8. Check it with the real CLI

```bash
SYMPHONY_REAL_COPILOT=1 dotnet test --filter RealCopilot
```

This runs two turns against the installed CLI with your login: one calls the tracker tool through Symphony's MCP gateway, the other writes a file in a resumed run. It spends a few AI credits.

## 9. Mix runners by status

`agent.runner_by_state` runs some statuses on another runner. Keys are tracker statuses, matched like `active_states`; values are `claude` or `copilot`. Statuses not listed use `agent.runner`.

```yaml
agent:
  runner: claude
  runner_by_state:
    Plan Aprobado: copilot        # development on Copilot, everything else on Claude

claude:
  model: claude-opus-5.5
copilot:
  model: claude-sonnet-5.5
  stall_timeout_ms: 1200000
```

- Each status uses its runner's section: its `model` and `model_by_state`, `stall_timeout_ms` and `resume`.
- **A session keeps its runner.** When a task moves to a status on another runner, the worker ends the session after the current turn, and the next dispatch starts a new session on the other runner with the full prompt, as for a model change. A session is never resumed on another runner.
- A runner name that isn't `claude` or `copilot` is a config error; a status that isn't active gets a startup warning.
- Gate and pull request warnings use the shortest `stall_timeout_ms` of the runners in use.
- `/line` shows the Agent next to each model, and the dashboard names the agent working on each card.
