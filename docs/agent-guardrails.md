# Agent guardrails (extension)

Three host-side checks keep an unattended agent from wasting turns or skipping the review handoff:

- **States an agent may reach** (`agent.allowed_states`): `update_state` only goes to the states the workflow allows.
- **Waiting for input** (`agent.max_idle_turns`): a session that stops making progress ends, and the issue waits until someone changes it in the tracker.
- **A startup check of the tracker's states**: Symphony warns when a work item type in the queue doesn't have a state the agents are meant to reach.

They came out of the first real runs on Azure Boards. Both runners hit an `update_state` to "Resolved", which an Agile **Task** doesn't have. Each then tried other states until "Closed" worked, which closed the item without a review and without its gate. In another run, Claude asked a question in a comment and then spent 80 continuation turns, about 15M tokens, re-reading the work item while it waited for an answer.

```yaml
agent:
  max_turns: 20
  max_idle_turns: 2                 # default 2; 0 turns the check off
  allowed_states: [Resolved]        # default: any state that is not terminal
```

## States an agent may reach

`update_state` is checked before any gate runs and before the tracker is called:

| `agent.allowed_states` | An agent may move an issue to |
|---|---|
| not set (default) | any state that is not in `tracker.terminal_states` |
| a list | only those states, matched like `active_states`. A terminal state is allowed if you list it. |

If a state is refused, nothing changes. The agent gets the error category `state_not_allowed` and a message telling it to leave the state as it is, explain in `add_comment`, and end its turn. The tool's description lists the policy, so the agent knows it before it tries. The refusal is in the audit trail like any other tool call.

The tracker can also reject a state, for example a state the work item type doesn't have (Azure DevOps answers 400, "not in the list of supported values"). The same "don't try other states" instruction is added to that error, so the agent stops instead of trying names until one works.

An empty list is a config error; leave the key out to allow every state that isn't terminal. Symphony warns at load when a gate's state or a `defects.states` entry isn't in `allowed_states`, because no agent could ever reach it.

People changing a status by hand are not affected.

## Waiting for input

After each turn the worker checks whether the turn did anything:

- **The workspace changed:** a hash of the refs, `HEAD`, `git status`, `git diff HEAD`, and the size and write time of each untracked file. Commits, pushes, new branches, edits and new files all count.
- **The tracker changed:** a successful tracker tool call that changes something other than a comment, such as a pull request or a defect report. A state change ends the session anyway.

Reading the work item and commenting ("still blocked") is not progress. After `agent.max_idle_turns` turns in a row without progress, the worker ends the session. The run is recorded with outcome `waiting`.

The orchestrator then **parks** the issue:

1. It posts a comment: "Symphony paused this task: … The agent is probably waiting for an answer; see its last comment. Reply with a comment or edit the work item, and Symphony will pick it up again on the next poll."
2. It reads the issue again and keeps its `UpdatedAt` as the baseline.
3. It doesn't dispatch the issue again, and doesn't schedule continuations, while the tracker shows the same `UpdatedAt` and state. The dashboard lists it under "Not eligible" as *Waiting for input*.
4. A reply, an edit or a state change wakes it on the next poll. The session that asked resumes when the runner's `resume` allows it, and its resume prompt says to read the latest comments first.

A parked issue that leaves the queue (moved to an inactive state, unassigned, tag removed) is forgotten. Parked issues are saved in `.symphony-state.json` (`waiting`), so a restart doesn't wake them.

On Azure Boards, adding a comment updates `System.ChangedDate`. On Wrike, check that a comment changes the task's `updatedDate` in your account; if it doesn't, edit the task or change its status to wake it.

A workspace that is not a git repository can't be checked, so its turns always count as progress. The check is then off, and only `max_turns`, `agent.max_fast_continuations` and the token budget limit a stuck session.

## Startup check of the tracker's states

On startup Symphony asks the tracker which states each queued work item type has, and logs a warning (`action=state_check outcome=warning`) for each gate state, `defects.states` entry or non-terminal `allowed_states` entry that a type lacks:

```
work item type "Task" has no state "Resolved" (its states: New, Active, Closed, Removed), so the gate on "Resolved" can't be reached for it; leave the type out of the queue or use a state it has
```

Azure Boards checks the types in `tracker.provider.work_item_types`; without that list there is nothing to check. Wrike is not checked. A failed check is logged (`outcome=skipped`) and never stops startup.

## Prompt advice

The guardrails limit the damage; the prompt decides how often they're needed. Tell the agent when to decide on its own and when to ask, for example:

```
## When the task is ambiguous
Take the most reasonable reading and keep going (for example, create a file that the task says to edit but
that doesn't exist). State every assumption in your handoff comment. Ask a question with add_comment only
when the choice is destructive or hard to undo, and then end your turn: don't poll the work item for an answer.
```
