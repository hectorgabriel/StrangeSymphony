# Audit trail (`audit`, extension)

Symphony keeps a record of who moved each task and what the agents did through the host: every run it started, every tool call an agent made (moves, comments, pull requests), the result of each verification gate, and every status change it saw in the tracker but didn't make. The trail is on by default.

```yaml
audit:
  path: /var/log/symphony/audit.jsonl   # optional; default <workspace.root>/.symphony-audit.jsonl
```

`path` may use `~` and `$VAR`, and a relative path is resolved against the workflow file's folder.

## Where to read it

- **`/audit`** on the status server lists the trail newest first. Filter by task (`/audit?issue=T12`). A banner says whether the chain is intact.
- **`GET /api/v1/audit`** returns the same as JSON: `lines`, `chain_intact`, `first_broken_line`, and `entries`, each with its `line` number and `intact`. Query parameters: `issue` (identifier or id) and `limit` (default 500, at most 5000; the newest lines are kept).
- **The file** is JSON Lines with snake_case keys, one record per line, so it's easy to ship to a log platform or SIEM.

## What a record says

Every line has `seq`, `at`, `kind`, `actor`, `issue_id` and `identifier`. `actor` is one of:

| Actor | Meaning |
|---|---|
| `symphony` | The orchestrator: it started or ended a run, released or closed a task |
| `agent` | An agent session called one of the host's tools |
| `outside` | The tracker showed a status Symphony didn't set: a person, or another integration. The tracker's own history says who |

| Kind | Written when | Main fields |
|---|---|---|
| `dispatched` | An agent run starts | `state`, `model`, `run_id`, `attempt`, `resumed`, `session_id` (when resuming) |
| `tool_call` | An agent calls a host tool: `update_state`, `add_comment`, `get_task`, `create_pull_request`, … | `tool`, `args`, `ok`, `category` (why it failed). For `update_state`: `from_state` and `state`, and `commit` and `branch` when a gate passed. `url` for a pull request |
| `run_ended` | The run ends | `outcome` (`completed`, `failed`, `stalled`, `released`, `budget`, `interrupted`), `error`, `turns`, `total_tokens`, `seconds` |
| `state_changed` | Symphony sees a status it didn't set | `from_state`, `state` |
| `released`, `closed` | Symphony lets a task go, or it reaches a terminal status | `state`, `reason` |

Records of one run share `run_id`, and those of one Claude Code session share `session_id`, which is also the name of Claude Code's own transcript.

A move that a gate refused appears as a `tool_call` with `ok: false` and the gate's category (`verification_failed`, `not_pushed`, …). A move that passed carries the commit the gate checked, so each status change points at the exact code that was verified.

### What is not copied into the trail

Tool arguments are summarized. `state` is kept verbatim, because it's the decision being audited. Every other argument, such as a comment's text or a pull request's title, is kept only as its length and the first 16 hex characters of its SHA-256. That is enough to match a record with the comment in the tracker, and the trail doesn't become another copy of the task's content.

## How outside changes are detected

Symphony knows the status it last set or saw for every task it has worked on. Each poll reads the active tasks, the running ones, and the released ones it still follows. A status that differs is written as `state_changed` with actor `outside`. Some consequences:

- Only tasks Symphony has worked on are followed.
- Changes are seen at poll granularity (`polling.interval_ms`). Two moves between polls appear as one.
- A read that started before Symphony's own move, or one that shows the old status less than 60 seconds after the move, is treated as a stale read, not as a move back.
- After a restart, Symphony picks up the last known statuses from the trail.

## Integrity

Each line carries `prev`, the hash of the line before it, and ends with `hash`, the SHA-256 of the line without that field. Editing, removing or reordering a line breaks the chain from that point on. `/audit`, the API and the log at startup (`action=audit_load outcome=chain_broken first_broken_line=…`) all report it. After a break, every following line is still checked against the one before it, so a single edit is reported once.

This makes changes **visible**, but it doesn't make them **impossible**. Someone who can write the file can rewrite it whole and recompute every hash. The agent runs as the same OS user as Symphony, so:

- Point `audit.path` outside `workspace.root`, which is the parent folder of every agent workspace.
- Ship the file to storage the agents can't write, such as a log platform, a SIEM or an append-only bucket, and keep the copy there as the record.
- For a stronger guarantee, record the latest `hash` somewhere separate from time to time. Every line before it is then fixed.

## What the trail does not cover

- **What the agent did inside its workspace** (shell commands, file edits) goes through Claude Code's own tools, not the host's. It's in Claude Code's session transcript (`session_id`) and, for the result, in the git history: each gated move records the commit.
- **Who the person was** behind an `outside` change. The tracker's history has that.
- **Changes Symphony made without an agent**, such as budget comments, are in the log (`action=budget_comment`), not in the trail.
