# Pull requests (`pull_request`, extension)

With a `pull_request` section in `WORKFLOW.md`, every agent session gets one more host-side tool, `create_pull_request`. The agent decides when its work is ready; the host decides whether it is fit to propose. Nothing is opened unless the pushed commit builds.

```yaml
pull_request:
  kind: azure_devops            # the only provider so far
  target_branch: master
  verify_command: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .symphony/build.ps1
  verify_timeout_ms: 900000     # default 15 minutes
  draft: true                   # default
  max_files: 200                # default
  forbidden_paths: [bin/, obj/, packages/, node_modules/, .vs/]   # default
  remote: origin                # default
  provider:
    organization_url: https://dev.azure.com/contoso
    project: My Project
    repository: my-repo         # name or id
    token: $AZURE_DEVOPS_PAT    # Code (Read & Write); this is also the default when token is omitted
```

`kind`, `target_branch` and `verify_command` are required. The section is validated when the workflow loads, like `tracker`, so a missing token or an unknown kind stops startup or a reload instead of failing in the middle of a run.

## What the tool does

The agent calls `create_pull_request` with a `title` and a Markdown `description`. The host then runs these checks in the workspace, in order, and stops at the first one that fails:

| Check | Fails with |
|---|---|
| `HEAD` is a branch, and not `target_branch` | `not_on_branch`, `invalid_branch` |
| No tracked file has uncommitted changes | `uncommitted_changes` |
| `<remote>/<branch>` exists and is exactly `HEAD` | `not_pushed` |
| The branch has commits against `target_branch` | `no_merge_base`, `no_changes` |
| No changed path matches `forbidden_paths` | `forbidden_paths` (lists the first ten) |
| At most `max_files` changed paths | `too_many_files` |
| `verify_command` exits 0 within `verify_timeout_ms` | `verification_failed` (ends with the last 8 KB of output) |
| The command left `HEAD` and the tracked files as they were | `uncommitted_changes`, `workspace_changed` |

Only then does it ask the provider. If the branch already has an active pull request into `target_branch`, the tool returns that one (`created: false`) instead of opening a second. Otherwise it opens one, as a draft unless `draft: false`, and appends a footer to the description with the commit, the command, how long it took and the number of changed files. A description longer than the provider allows (4,000 characters on Azure Repos) is cut before the footer, so the footer always survives.

The result is `{created, id, url, is_draft, verified_commit, changed_files, verify_seconds}`. The tool doesn't touch the tracker: the prompt should tell the agent to attach `url` with the tracker's link tool and then move the task to its review state.

Why these checks run on the host and not in the prompt:

- **The agent can't vouch for its own build.** It may have compiled one project, or the wrong commit. The host builds exactly the commit it is about to propose.
- **Diffs that carry build output get stopped.** A repository without a `.gitignore` plus `git add -A` sends `packages/` and `bin/` to review; `forbidden_paths` and `max_files` catch it before the build even starts.
- **The token stays with the host.** The PAT is read by Symphony, used only for the provider's REST calls, and removed from the agent's environment along with the tracker's credentials.

## Paths in `forbidden_paths`

Matching is case-insensitive and uses forward slashes on every OS.

- `dir/` matches that directory at any depth: `bin/` catches `src/App/bin/Debug/App.dll`, but not `src/binder/Binder.cs`.
- `*.ext` matches by extension: `*.nupkg`.
- Anything else matches a file name exactly: `secrets.json`.

Set `forbidden_paths: []` to turn the check off.

## Timeouts

A tool call produces no agent events, so to the orchestrator a long build looks like a quiet session. The tool's own limit is `verify_timeout_ms` plus three minutes for git and the provider; if that reaches `claude.stall_timeout_ms`, the workflow loads with a warning, because the session could be stopped as stalled in the middle of a build. Keep `verify_timeout_ms` comfortably below the stall timeout, and remember that the first build in a fresh clone (package restore included) is the slow one.

## Azure Repos

- `organization_url`, `project` and `repository` accept `$VAR`, like the tracker's settings.
- The PAT needs **Code: Read & Write**. A bad or expired token makes Azure DevOps answer 203 with a sign-in page; the tool reports it as an authorization error that names the scope.
- The returned `url` is the web page (`…/_git/<repo>/pullrequest/<id>`), not the REST resource.
- `request_timeout_ms` (default 30000) and `api_version` (default `7.1`) can be set under `provider`.
