# Azure DevOps (Azure Boards) adapter profile

The adapter profile required by SPEC §11.2.

## Kind

`tracker.kind: azure_devops`

## `tracker.provider` keys

| Key | Type | Default | Notes |
|---|---|---|---|
| `organization_url` | string, `$VAR` allowed | **required** | Absolute http(s) URL, for example `https://dev.azure.com/contoso`. Azure DevOps Server collection URLs also work. |
| `project` | string, `$VAR` allowed | **required** | Project name. It defines the scope. |
| `token` | **secret**, `$VAR` | `$AZURE_DEVOPS_PAT` when omitted | Personal access token. It needs **Work Items: Read & Write** (Read is enough if you don't use the tools). An empty resolution counts as missing. Literal tokens work but are discouraged (SPEC §15.3). |
| `assigned_to` | string | none (every item can be dispatched) | `@me` means the identity behind the PAT, resolved once with `connectionData`. You can also give an identity GUID, or a unique name or display name (case-insensitive). |
| `area_path` | string | none | Limits the scope to `[System.AreaPath] UNDER '<value>'`. |
| `work_item_types` | list of strings | none (all types) | Limits the scope to these types. |
| `identifier_prefix` | string | `WI` | `identifier` becomes `<prefix>-<id>`. |
| `block_on_predecessors` | bool | `true` | Open predecessors make an item non-dispatchable. |
| `api_version` | string | `7.1` | REST API version for WIQL, batch and update calls. Comments always use `7.1-preview.4`. |
| `request_timeout_ms` | int ≥ 1000 | `30000` | Per-request HTTP timeout. |
| `wiql_page_size` | int ≥ 1 | `1000` | WIQL keyset page size. |

**State defaults.** `active_states` is **required**, because each process template uses different names. `terminal_states` defaults to `[Done, Closed, Removed]`.

**Validation errors.** These surface as `WorkflowException.Code` at startup and on reload:

- `invalid_tracker_config`: `organization_url` or `project` is missing, the URL is invalid, or a key has the wrong type.
- `missing_tracker_secret`: the token is unset or empty, or its `$VAR` reference is.

## Secret environment names

These are removed from the agent's environment: `AZURE_DEVOPS_PAT`, `AZURE_DEVOPS_EXT_PAT`, `SYSTEM_ACCESSTOKEN`, `AZURE_DEVOPS_TOKEN`, and whatever variable `token: $VAR` points at.

## Scope selection and pagination

`fetch_issues_by_states` runs a WIQL query with these conditions:

```
[System.TeamProject] = @project AND [System.State] IN (...) AND [System.Id] > <last>
  [AND [System.AreaPath] UNDER ...] [AND [System.WorkItemType] IN (...)]
ORDER BY [System.Id] ASC          ($top = wiql_page_size)
```

Paging is keyset-based on `System.Id`. A page that doesn't move past the previous last ID fails with `tracker_pagination`. The adapter then fetches details with `POST _apis/wit/workitemsbatch` in chunks of 200, using `$expand=Relations` and `errorPolicy=Omit`. Results come back in WIQL order (ascending ID). **Active items assigned to other people are still returned, with `dispatchable=false`**, because the scheduler owns that filter (SPEC §11.1).

`fetch_issues_by_ids` uses the same batch call. An item is left out when it doesn't exist, isn't readable, or no longer matches the project, area or type scope.

## Field mapping

| Issue field | Source |
|---|---|
| `id` | Work item ID as a string. This is also the dispatch identity. |
| `identifier` | `<identifier_prefix>-<id>` |
| `title` | `System.Title` (required) |
| `description` | HTML-to-text of `System.Description`, then `Microsoft.VSTS.TCM.ReproSteps` and `Microsoft.VSTS.Common.AcceptanceCriteria`, each under a heading. `null` when all are empty. |
| `priority` | `Microsoft.VSTS.Common.Priority` (1–4 in the standard processes), otherwise `null` |
| `state` | `System.State` (required), spelled exactly as Azure DevOps returns it |
| `branch_name` | Always `null` |
| `url` | `<org>/<project>/_workitems/edit/<id>` |
| `assignee_id` | `System.AssignedTo.id` |
| `labels` | `System.Tags`: split on `;`, trimmed, lowercased, blanks dropped, duplicates removed |
| `blocked_by` | Predecessor links (`System.LinkTypes.Dependency-Reverse`), each with `{id, identifier, state}`. Predecessors that can't be read are dropped, not guessed. |
| `created_at` / `updated_at` | `System.CreatedDate` / `System.ChangedDate` (RFC 3339), otherwise `null` |
| `native_ref` | `{organization_url, project, work_item_id, work_item_type, rev, area_path, iteration_path, assigned_to}` |

`dispatchable` is true only when both of these hold:

1. The `assigned_to` setting is unset, or matches the item's assignee.
2. `block_on_predecessors` is false, or every readable predecessor is in a configured terminal state.

**Malformed records.** A record is malformed when it has no numeric ID, title or state.

- In a state-list query, malformed records are **left out and logged** (`action=tracker_normalize outcome=omitted`).
- In an ID refresh, a malformed record **fails the call** with `tracker_response`.

## Agent tools

These tools are served to Claude Code as the MCP server `tracker`, so the agent sees names like `mcp__tracker__add_comment`. Every call runs inside the Symphony process with the adapter's PAT. **Every tool acts only on the session's current work item**, and the agent can't choose another ID.

| Tool | Changes data | Input | Result |
|---|---|---|---|
| `get_work_item` | no | `{comment_count?: 0..50}` (default 10) | Fields, tags, description, relations and recent comments |
| `add_comment` | yes | `{text}` (Markdown) | `{comment_id, work_item_id}` |
| `update_state` | yes | `{state, reason?}` | `{work_item_id, state, rev}`. Fails if the process rules don't allow the transition. |
| `add_link` | yes | `{url, comment?}`, where `url` must be http(s) | `{work_item_id, url}` (adds a `Hyperlink` relation) |

**Errors.** A failed tool returns a structured result, `isError: true` with `{error: {category, message}}`, and the session keeps going. The categories are:

- the tracker categories listed below
- `invalid_arguments`
- `unsupported_tool`
- `not_found`
- `timeout` (60 s per call)

**Idempotency.** Adding a comment or a link is not idempotent; calling it twice creates two. Setting a state to the value it already has does nothing. Azure DevOps rate limits show up as `tracker_rate_limited`.

## Error mapping

Every adapter error is a `Symphony.Core.Tracker.TrackerException` with a `Category` and a `Message`. The PAT never appears in a message. Where available, the exception also carries `ProviderStatus` and `RetryAfterMs`.

| Condition | Category |
|---|---|
| Unsupported kind | `unsupported_tracker_kind` |
| Bad provider config | `invalid_tracker_config` |
| PAT missing or empty | `missing_tracker_secret` |
| DNS, connect, TLS or timeout failure | `tracker_request` |
| 401, 403, an HTML response with status 203 (Azure DevOps's sign-in page, which means a bad PAT), or any other non-2xx status | `tracker_status` |
| 429 | `tracker_rate_limited` (includes `RetryAfterMs` when `Retry-After` is set) |
| Invalid JSON, or an unexpected payload shape or malformed refresh record | `tracker_response` |
| WIQL keyset paging didn't advance | `tracker_pagination` |
