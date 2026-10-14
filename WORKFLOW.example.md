---
# Copy to WORKFLOW.md in the repository you want Symphony to work on, then adjust.
tracker:
  kind: azure_devops
  provider:
    organization_url: https://dev.azure.com/your-org
    project: Your Project
    # Host-side secret reference; never put a literal PAT in this file.
    # Omit `token` to fall back to $AZURE_DEVOPS_PAT.
    token: $AZURE_DEVOPS_PAT
    # Per-member mode: each teammate runs their own Symphony and only picks up
    # work items assigned to the identity behind their own PAT.
    assigned_to: "@me"
    # area_path: "Your Project\\Team A"
    # work_item_types: [User Story, Bug, Task]
    # block_on_predecessors: true
  # Only items carrying every label (Azure DevOps tag) are dispatched.
  required_labels: [agent]
  # Agile process example. Scrum: [Approved, Committed]; Basic: [To Do, Doing].
  active_states: [New, Active]
  terminal_states: [Closed, Removed]

# Wrike instead of Azure Boards: the queue is the subtasks of one Wrike task.
# tracker:
#   kind: wrike
#   provider:
#     parent_task_id: "https://www.wrike.com/open.htm?id=4583302343"
#     host: www.wrike.com              # or app-eu.wrike.com / app-us2.wrike.com
#     token: $WRIKE_TOKEN
#     assigned_to: "@me"
#     # workflow: "ABSEAL I&N"         # defaults to the queue task's workflow
#   # Custom status names of that workflow. Hand-off status (for the prompt): Analizado.
#   active_states: [Active, En Proceso]
#   terminal_states: [Completed, Cancelled]

polling:
  interval_ms: 30000

workspace:
  # On Windows prefer a short path, with forward slashes or in single quotes: 'C:/sym/ws'
  root: ~/symphony_workspaces

hooks:
  timeout_ms: 300000
  # auto (default) = bash -lc; on Windows that is Git Bash, so these hooks work there unchanged.
  # Alternatives: bash | powershell | pwsh | an absolute path to a bash.
  # shell: auto
  after_create: |
    git clone --depth 50 https://dev.azure.com/your-org/Your%20Project/_git/your-repo .
  before_run: |
    git fetch origin && git status --short

agent:
  # Keep this low on a Pro plan; each session draws from your own usage limits.
  max_concurrent_agents: 2
  max_turns: 20
  max_retry_backoff_ms: 300000
  # Sessions that end with the issue in the same state retry after 1 s this many times, then back off.
  max_fast_continuations: 3

claude:
  command: claude
  model: sonnet
  # Unattended default: file edits auto-approved; anything else must be allow-listed.
  # Anything not allowed is denied automatically (never waits for a human).
  permission_mode: acceptEdits
  allowed_tools:
    - "Bash(git *)"
    - "Bash(dotnet build *)"
    - "Bash(dotnet test *)"
  disallowed_tools: [AskUserQuestion, WebFetch]
  stall_timeout_ms: 900000

# server:
#   port: 4510   # dashboard + JSON API on 127.0.0.1
---
You are working on Azure Boards work item {{ issue.identifier }}: "{{ issue.title }}".
Work item URL: {{ issue.url }}
Current state: {{ issue.state }}
{% if issue.labels.size > 0 %}Tags: {{ issue.labels | join: ", " }}{% endif %}

## Description
{{ issue.description }}

{% if attempt %}
This is retry/continuation attempt {{ attempt }}. Check the workspace and the work item comments
for progress from earlier attempts before starting over.
{% endif %}

## How to work
1. Use the `get_work_item` tool to read the latest details and comments.
2. Implement the change in this repository on a branch named `wi/{{ issue.identifier }}`.
3. Build and run the tests; fix failures.
4. Commit and push the branch, then post a short summary with `add_comment`
   (what changed, how it was verified, anything a reviewer should look at).
5. Move the work item to "Resolved" with `update_state` when the work is ready for human review.
   If you are blocked, explain why with `add_comment` and leave the state unchanged.
