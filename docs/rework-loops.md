# Why work goes back (`defects`, extension)

Counting rework runs tells you how much work repeats. The `defects` section tells you **why**: when an agent sends a task back, for example the tester moving it to *Con Defectos*, it first reports its defects by category and severity. The Metrics page (`/line`) then shows which categories send work back, whose work it was, how long triage takes and what each loop cost.

```yaml
defects:
  states: [Con Defectos]          # statuses that send work back; a single name is fine too
  categories:                     # a list of names, or a map of name to description
    acceptance_criteria: An acceptance criterion of the analysis isn't met
    business_rule: A business rule or calculation is wrong
    validation: Missing or wrong validation of user input
    ui: Layout, labels, navigation or the UI/UX checklist
    regression: Something that worked before is broken
```

Category names are lowercase letters, digits and `_`, at most 40 characters. The descriptions go into the tool's description, so the agent knows what each one means. Severities are fixed: `critical`, `major` and `minor`.

## What the agent sees

With `defects` set, every session gets one more host tool, **`report_defects`**. It takes a list with one entry per defect, each with a `category` and a `severity`, and nothing else. The schema allows no free text.

`update_state` to one of `defects.states` is **refused** with `defects_not_reported` until the session has called `report_defects`. That way every loop has its reasons. Calling `report_defects` again replaces the earlier list. After a successful move back, the next one needs a new report.

The check runs before any verification gate, so a missing report costs no build. Moves to other statuses are unaffected. A move a person makes in the tracker isn't checked, because Symphony doesn't make it.

The defects themselves still go in tracker comments, as before. Symphony keeps only the counts. The audit trail records the `report_defects` call with those counts, not fingerprints, because category and severity names are not task content.

## What the Metrics page shows

The **Why work goes back** section of `/line`, and `rework` in `GET /api/v1/metrics`:

| Field | Meaning |
|---|---|
| `loops`, `defects` | Times an agent sent a task back, and the defects it reported |
| `issues_with_loops` | Tasks sent back at least once |
| `closed_without_rework`, `closed_issues` | Closed tasks that never had a loop or a rework run |
| `avg_triage_seconds` | From the send-back to the next agent run: the time a person took to triage |
| `next_states` | Where that next run started: `Plan Aprobado` means a fix; `waiting` means nobody has picked it up yet |
| `loop_rework_runs`, `loop_rework_tokens`, `loop_rework_seconds` | What loops cost: rework runs from the send-back until the task got past the stage that sent it back |
| `other_rework_runs`, `other_rework_tokens` | Rework no report explains: a person sent the task back, for example from review |
| `by_severity` | Defects per severity |
| `by_category` | Per category: defects, severity split, loops, tasks, and the rework tokens of the loops it was part of |
| `by_source` | Loops by the run whose work was sent back, as status and model, such as `Plan Aprobado` and `sonnet` |
| `recent` | The last 30 loops, each with its defect counts, who reported it, whose work it was, triage and rework |

Each task in `issues` also has `loops`.

### How a loop is put together

The metrics log gets one `defects` line per move back, with `defect_counts` (category to severity to count), the status it came from and the reporting run's model. The report then follows the task's lines:

- **Whose work:** the latest run before the report in a status other than the reporter's. For the tester, that's usually the developer's run in Plan Aprobado.
- **Triage:** the first run that started after the report.
- **Rework:** every rework run from then on, until the task is handed off to another status after a run in the reporting stage again (the re-test passed), the task closes, or the next loop starts.

A loop with several categories counts toward each of them in `by_category`, so the rework tokens in that table don't add up to the total.

## Limits

- **Only agent send-backs carry a report.** A reviewer sending a task from En Revision back to Plan Aprobado shows up as `other_rework_runs`, without categories. If those runs grow, the reasons are in the review comments.
- **The categories are the agent's call.** Check a sample against the tracker comments from time to time, and tighten a category's description if it's being misused.
- **Renaming a category** starts a new row. Old lines keep the old name.
