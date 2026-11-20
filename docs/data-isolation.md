# Keeping database credentials away from agents (`workspace.strip_env`, extension)

An agent can only connect to a database if it can find a way in: a connection string, a password, or a host that accepts it. Symphony decides what every process it starts can see. `workspace.strip_env` lets you take database credentials out of that view.

```yaml
workspace:
  root: ~/symphony_sdlc_ws
  strip_env:
    - ConnectionStrings__*     # .NET configuration: ConnectionStrings__Ventas, ...
    - "*_DATABASE_URL"
    - DATABASE_URL
    - PGPASSWORD
    - SQLCMDPASSWORD
```

Each entry is a variable name, or a pattern where `*` matches any run of characters. Matching ignores case. A single name may be given as a plain string instead of a list.

## What gets stripped, and from what

The matching variables are removed from the environment of **every process Symphony starts in a workspace**:

| Process | Why it matters |
|---|---|
| Claude Code, and so every command the agent runs | The agent's shell, scripts and tools inherit Claude Code's environment |
| Hooks (`after_create`, `before_run`, `after_run`, `before_remove`) | `before_run` and `after_run` run in a workspace that holds the agent's code |
| Verification gate commands (`gates.*.run`) | They build and test the agent's code |
| `pull_request.verify_command` | The same |

The tracker's credentials and the pull request token are always removed from all of these except the hooks; `strip_env` adds to that list and also covers the hooks. Symphony itself keeps its whole environment, so the tracker and pull request integrations go on working.

The workflow is rejected at load if an entry is empty, contains `=` or spaces at either end, or would also remove `PATH` or `HOME` (`*`, `P*`): with those gone the agent and the builds can't start.

## The load-time warning

Each time the workflow loads, Symphony looks through its own environment for names that usually hold database access (`ConnectionStrings__*`, `*CONNECTION_STRING*`, `*DATABASE_URL*`, `DB_*`, `*_DB_*`, `PGPASSWORD`, `PGHOST`, `MYSQL_*`, `SQLCMD*`, `MONGO*`, `*REDIS_URL*` and a few others). If any of them isn't covered by `strip_env`, it logs the names, never the values:

```
action=workflow_load outcome=warning reason=database_credentials_in_environment names=ConnectionStrings__Ventas,PGPASSWORD ...
```

Treat that warning as a failed check. Either add the names to `strip_env` or, better, start Symphony without them.

## What `strip_env` does not cover

Stripping variables closes the easiest way in, not every way. For the "agents must not reach the database" rule, combine it with these, roughly in order of strength:

1. **Network.** Block the database ports from the machine, or the OS user, that runs Symphony: a firewall rule, a separate VLAN, or a security group. That holds even if a credential leaks some other way, and it's the control to show the committee.
2. **A dedicated OS user.** Run Symphony as a user that has no database credentials at all: no `~/.pgpass`, no .NET user secrets (`~/.microsoft/usersecrets`), no `~/.my.cnf`, no SQL Server aliases, no cloud CLI logins that can fetch database passwords. The agent can read whatever that user can.
3. **Repositories.** No connection strings with real hosts or passwords in `appsettings*.json`, `.env` files or test fixtures. Tests should use an in-memory database or a throwaway container on the test machine.
4. **`strip_env`** for whatever has to stay in Symphony's environment, and the warning above to catch what was missed.
5. **A self-hosted model** (see [hybrid-models.md](hybrid-models.md)) for stages that handle real data, so that what the agent does read doesn't leave your network.

[`WORKFLOW.sdlc.example.md`](../WORKFLOW.sdlc.example.md) has a starting `strip_env` list.
