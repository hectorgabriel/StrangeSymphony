---
# SDLC pipeline: one Wrike queue, one role per status. See docs/sdlc-pipeline.md.
# Copy to WORKFLOW.md in the target repository (Magnacero.Xaf), which must already have
# BossDream installed and the role agents in .claude/agents/.
tracker:
  kind: wrike
  provider:
    parent_task_id: "https://www.wrike.com/open.htm?id=4583302343"
    host: www.wrike.com
    token: $WRIKE_TOKEN
    assigned_to: "@me"
    block_on_predecessors: true
  # Only statuses where an agent works. Human gates (Analizado, Plan Propuesto, Con Defectos,
  # En Revision, Documentado, Requiere Informacion) must stay out of this list, or the next
  # role starts before anyone reviews the previous one.
  active_states: [Por Analizar, Por Desarrollar, Plan Aprobado, Por Probar, Por Documentar]
  terminal_states: [Completado, Cancelado]

polling:
  interval_ms: 30000

workspace:
  # The workspace is kept across stages until the task reaches a terminal status.
  root: ~/symphony_sdlc_ws

hooks:
  timeout_ms: 600000
  after_create: |
    git clone https://dev.azure.com/your-org/Your%20Project/_git/Magnacero.Xaf .
  before_run: |
    git fetch origin --prune && git status --short

agent:
  max_concurrent_agents: 1
  max_turns: 10
  max_retry_backoff_ms: 300000
  max_fast_continuations: 3

claude:
  command: claude
  model: sonnet
  permission_mode: acceptEdits
  allowed_tools:
    - "Bash(git *)"
    - "Bash(dotnet build)"
    - "Bash(dotnet build *)"
    - "Bash(dotnet test)"
    - "Bash(dotnet test *)"
    - "Bash(dotnet format)"
    - "Bash(dotnet format *)"
  disallowed_tools: [AskUserQuestion, WebFetch]
  stall_timeout_ms: 900000

server:
  port: 4511
---
{% assign stage = issue.state | downcase %}
You are working on Wrike task {{ issue.identifier }}: "{{ issue.title }}".
Task URL: {{ issue.url }}
Current status: {{ issue.state }}

## Description
{{ issue.description }}

{% if attempt %}
This is retry/continuation attempt {{ attempt }}. Check the workspace, the branch and the task
comments for progress from earlier attempts before starting over.
{% endif %}

## Rules for every stage
- You run unattended: nobody can answer a question during the session. Wherever your role file says
  to ask the user, stakeholder or another agent, post the question with `add_comment` and move the
  task to "Requiere Informacion" with `update_state`, then stop. Never guess an answer to it.
- Wrike is the only tracker. Report findings, defects and hand-offs as Wrike comments, never in
  Azure Boards or as .docx files.
- Write Wrike comments in Spanish, briefly and in plain language: the business team reads them.
- Run each command on its own (`git`, `dotnet build`, `dotnet test`, `dotnet format`); do not chain
  them with `;` or `&&`. A denied command cannot be retried in the same session.
- Start by reading the task and its latest comments with `get_task`.
- The line starting with `/bossdream-mode` below sets the BossDream mode for this stage. Do not
  change the mode yourself; the hooks block edits the mode doesn't allow.

{% if stage == "por analizar" %}
## Stage: analysis
BossDream mode: /bossdream-mode plan

Your role is defined in `.claude/agents/analyst.md`. Read it and follow it, with these overrides:
1. Create `analisis/{{ issue.identifier | downcase }}-<short-slug>` from `origin/dev`.
2. Write the functional scope document, the user stories and the technical analysis in
   `docs/analisis/<epica>/`. Fields only a stakeholder can give (request number, Product Owner,
   business area, owner) come from the task description; when one is missing, write
   `PENDIENTE` in the document and list it in your comment instead of inventing it.
3. Commit and push the branch, then post a comment with the branch name, the document paths and the
   open `PENDIENTE` items.
4. Move the task to "Analizado".
{% elsif stage == "por desarrollar" %}
## Stage: implementation plan
BossDream mode: /bossdream-mode plan

Your role is defined in `.claude/agents/developer.md`. Read it and follow it, with these overrides:
1. Create `feature/{{ issue.identifier | downcase }}-<short-slug>` (or `fix/` for a defect) from the
   analysis branch `origin/analisis/{{ issue.identifier | downcase }}-*` when it exists, otherwise
   from `origin/dev`.
2. Do not write code in this stage. Break the requirement into tasks and write them in
   `plans/{{ issue.identifier | downcase }}/tasks.md` as `- [ ]` checkboxes, one concrete task per
   line. This file is the task list your role asks the developer to approve.
3. Commit and push, then post the task list as a comment.
4. Move the task to "Plan Propuesto". A person approves it by moving the task to "Plan Aprobado".
{% elsif stage == "plan aprobado" %}
## Stage: implementation
BossDream mode: /bossdream-mode implement

Your role is defined in `.claude/agents/developer.md`, and the approved task list is
`plans/{{ issue.identifier | downcase }}/tasks.md` on the branch
`origin/feature/{{ issue.identifier | downcase }}-*` (or `fix/`). Check it out and follow the role:
1. Read the latest comments first: the reviewer may have changed the plan or listed defects to fix.
2. Implement the tasks in order and tick each one (`- [x]`) as you finish it. If the plan turns out
   to be wrong or incomplete, comment why and move the task to "Requiere Informacion".
3. Also apply the checklist in `.claude/agents/uiux.md` to every screen, action and validation
   message you touched.
4. The BossDream Stop hook runs the quality pipeline and blocks completion while a gate fails or a
   task is unchecked. Fix failures; do not skip or ignore tests.
5. Commit and push, then post a comment: what changed, how it was verified, what to review.
6. Move the task to "Por Probar".
{% elsif stage == "por probar" %}
## Stage: verification
BossDream mode: /bossdream-mode implement

Your role is defined in `.claude/agents/tester.md`. Work on the task's feature or fix branch
(`origin/feature/{{ issue.identifier | downcase }}-*` or `origin/fix/...`), with these overrides:
1. Derive the test cases from the acceptance criteria in `docs/analisis/<epica>/` and write them in
   `docs/pruebas/<epica-o-modulo>/`.
2. Add or complete the automated unit and integration tests and run them. Leave E2E (TestCafe) and
   Postman suites out of this stage.
3. Report each defect as a Wrike comment with the fields of the RY-01AC01-R02 format (ID, type,
   impact, module, process, description, steps to reproduce). Do not fix production code yourself.
4. Commit and push your tests and test cases.
5. If you found defects, move the task to "Con Defectos". Otherwise post a summary of what was
   verified and move it to "En Revision".
{% elsif stage == "por documentar" %}
## Stage: user manual
BossDream mode: /bossdream-mode document

Your role is defined in `.claude/agents/documentador.md`. The change is already merged into `dev`.
1. Create `manual/{{ issue.identifier | downcase }}-<short-slug>` from `origin/dev`.
2. Write the manual in `docs/manuales/<modulo>/` from what is actually in `dev`, using the
   analysis and test documents only as context.
3. Commit and push, then post a comment with the branch and the manual's path.
4. Move the task to "Documentado".
{% else %}
## Stage: unknown
Status "{{ issue.state }}" has no stage in this workflow. Post a comment saying so and stop without
changing anything.
{% endif %}
