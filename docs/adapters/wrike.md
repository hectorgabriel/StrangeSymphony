# Wrike adapter profile

The adapter profile required by SPEC §11.2.

## Kind

`tracker.kind: wrike`

## The queue

Symphony works on the **subtasks of one Wrike task**, the queue task (for example a task named "Agent"). To hand work to the agent, add a task as a subtask of the queue task. A task that stops being a subtask of the queue task leaves Symphony's scope.

## `tracker.provider` keys

| Key | Type | Default | Notes |
|---|---|---|---|
| `parent_task_id` | string, `$VAR` allowed | **required** | The queue task. Any of these forms works: the v4 task ID (`MAAAAAERL6TH`), the numeric permalink ID (`4583302343`), or the permalink URL (`https://www.wrike.com/open.htm?id=4583302343`). A numeric ID is converted once with `GET /ids`. |
| `host` | string, `$VAR` allowed | `www.wrike.com` | The account's data center, for example `app-eu.wrike.com` or `app-us2.wrike.com`. Only `wrike.com` hosts over https are accepted, because the token is sent there. |
| `token` | **secret**, `$VAR` | `$WRIKE_TOKEN` when omitted | A permanent access token (Wrike: Apps & Integrations → API) or an OAuth token. It needs read access to the queue task and its subtasks, and write access if you use the tools. Literal tokens work but are discouraged (SPEC §15.3). |
| `workflow` | string | the queue task's workflow | The workflow whose status names `active_states`, `terminal_states` and `update_state` use. You can give its name or ID. When several workflows share the name, the visible one wins; use the ID if that is still ambiguous. |
| `assigned_to` | string | none (every task can be dispatched) | `@me` means the user behind the token, resolved once with `GET /contacts?me=true`. You can also give a contact ID such as `KUAIFIRR`. A task matches when the ID is among its assignees. |
| `identifier_prefix` | string | `WR` | `identifier` becomes `<prefix>-<permalink id>`. |
| `block_on_predecessors` | bool | `true` | Open predecessors make a task non-dispatchable. |
| `request_timeout_ms` | int ≥ 1000 | `30000` | Per-request HTTP timeout. |

**State defaults.** `active_states` is **required**, because every Wrike workflow has its own custom statuses. `terminal_states` defaults to `[Completed, Cancelled]`, the standard Wrike names; set them if your workflow names them differently.

**Status names.** States are custom status names, compared case-insensitively. Wrike allows the same name more than once in a workflow (often a visible status plus hidden older ones):

- When a task's state is read, the name of its own status is used.
- When a name is turned into a status (`update_state`), the visible status wins. A name that still matches several statuses is rejected rather than guessed.

**Validation errors.** These surface as `WorkflowException.Code` at startup and on reload:

- `invalid_tracker_config`: `parent_task_id` is missing or malformed, `host` is not a Wrike host, or a key has the wrong type.
- `missing_tracker_secret`: the token is unset or empty, or its `$VAR` reference is.

These checks need the API, so they run on the first poll and fail it with `invalid_tracker_config` until fixed:

- the queue task isn't visible to the token;
- the workflow can't be determined;
- a configured state isn't a status of that workflow.

## Secret environment names

These are removed from the agent's environment: `WRIKE_TOKEN`, `WRIKE_ACCESS_TOKEN`, and whatever variable `token: $VAR` points at.

## Scope selection

On first use, the adapter resolves:

- the queue task's v4 ID;
- the workflow's statuses (`GET /workflows`);
- the `@me` contact.

They are cached for the adapter's life. A task with a status ID the cache doesn't know (a status added later) triggers one reload of the workflows.

`fetch_issues_by_states` runs these steps:

1. Reads the queue task's `subTaskIds`.
2. Loads the subtasks with `GET /tasks/{ids}` in batches of 100.
3. Keeps those whose status name is one of the requested states.

When a batch fails because one ID is invalid (Wrike fails the whole batch with `400 invalid_request`), its IDs are fetched one at a time and the invalid ones are skipped. **Tasks assigned to other people are still returned, with `dispatchable=false`**, because the scheduler owns that filter (SPEC §11.1).

A batch get doesn't return descriptions. The adapter fetches each description with `GET /tasks/{id}` and caches it until the task's `updatedDate` changes.

`fetch_issues_by_ids` uses the same batch call. It leaves out tasks that don't exist, aren't visible, or are no longer subtasks of the queue task.

## Field mapping

| Issue field | Source |
|---|---|
| `id` | The v4 task ID. This is also the dispatch identity. |
| `identifier` | `<identifier_prefix>-<numeric id from permalink>`, for example `WR-4583302343`. Branch names stay readable. |
| `title` | `title` (required) |
| `description` | HTML-to-text of `description`, or `null` when empty |
| `priority` | `importance`: High → 1, Normal → 2, Low → 3 |
| `state` | The name of the task's custom status (required) |
| `branch_name` | Always `null` |
| `url` | `permalink` |
| `assignee_id` | The first of `responsibleIds` |
| `labels` | Always empty: the queue task defines the scope, so leave `required_labels` unset |
| `blocked_by` | Each dependency where the task is the successor, as `{id, identifier, state}`. Predecessors that can't be read are dropped, not guessed. All relation types count. |
| `created_at` / `updated_at` | `createdDate` / `updatedDate`, otherwise `null` |
| `native_ref` | `{task_id, permalink, parent_task_id, custom_status_id, status_group, importance}` |

`dispatchable` is true only when both of these hold:

1. The `assigned_to` setting is unset, or its contact is among the task's assignees.
2. `block_on_predecessors` is false, or every readable predecessor is in a configured terminal state.

Dependencies are read with `GET /tasks/{id}/dependencies`, only for tasks whose `dependencyIds` is not empty.

**Malformed records.** A record is malformed when it has no ID, title or known status.

- In a state-list query, malformed records are **left out and logged** (`action=tracker_normalize outcome=omitted`).
- In an ID refresh, a malformed record **fails the call** with `tracker_response`.

## Agent tools

These tools are served to Claude Code as the MCP server `tracker`, so the agent sees names like `mcp__tracker__add_comment`. Every call runs inside the Symphony process with the adapter's token. **Every tool acts only on the session's current task**, and the agent can't choose another ID.

| Tool | Changes data | Input | Result |
|---|---|---|---|
| `get_task` | no | `{comment_count?: 0..50}` (default 10) | State, `valid_states`, title, importance, assignees, description, `blocked_by` and the most recent comments |
| `add_comment` | yes | `{text}` (Markdown) | `{comment_id, task_id}` |
| `update_state` | yes | `{state}`, a status name of the queue workflow | `{task_id, state}`. An unknown or ambiguous name fails with `invalid_arguments` and the list of valid statuses. |
| `add_link` | yes | `{url, comment?}`, where `url` must be http(s) | `{task_id, url}`. Wrike has no link relations, so this posts a comment containing the link. |

**Comment formatting.** `add_comment` converts a small Markdown subset to Wrike's comment HTML:

- line breaks;
- `- ` and `* ` lists;
- `**bold**`;
- `` `code` `` (inline code, escaped as written, with no bold or links inside);
- `[text](https://…)` links and bare http(s) URLs.

Everything else is HTML-escaped, so the text shows as written.

**Errors.** A failed tool returns a structured result, `isError: true` with `{error: {category, message}}`, and the session keeps going. The categories are:

- the tracker categories listed below
- `invalid_arguments`
- `invalid_context`
- `unsupported_tool`
- `not_found`
- `timeout` (60 s per call)

**Idempotency.** Adding a comment or a link is not idempotent; calling it twice creates two. Setting a status to the value it already has does nothing.

## Error mapping

Every adapter error is a `Symphony.Core.Tracker.TrackerException` with a `Category` and a `Message`. The token never appears in a message. Where available, the exception also carries `ProviderStatus` and `RetryAfterMs`. Wrike's `error` code is kept in `Exception.Data["wrike_error"]`.

| Condition | Category |
|---|---|
| Unsupported kind | `unsupported_tracker_kind` |
| Bad provider config, an invisible queue task, an unknown workflow or state | `invalid_tracker_config` |
| Token missing or empty | `missing_tracker_secret` |
| DNS, connect, TLS or timeout failure | `tracker_request` |
| 401, 403 or any other non-2xx status | `tracker_status`. The message includes Wrike's `error` code and `errorDescription`. |
| 429 | `tracker_rate_limited` (includes `RetryAfterMs` when `Retry-After` is set) |
| Invalid JSON, a response without `data`, or a malformed refresh record | `tracker_response` |

## Live check

`RealWrikeTests` (in `tests/Symphony.Tests/WrikeAdapterTests.cs`) reads a real queue and writes nothing. It is skipped unless you set these:

```bash
SYMPHONY_REAL_WRIKE=1 WRIKE_PARENT_TASK=4583302343 WRIKE_ACTIVE_STATES="Active,En Proceso" \
  dotnet test --filter "FullyQualifiedName~RealWrikeTests"
```

`WRIKE_TOKEN` must be set as well.
