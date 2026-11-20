# Sample metrics and audit trail

Made-up data for checking the Metrics page (`/line`), the audit page (`/audit`) and their APIs without running real agents: 14 tasks over the last 13 days of the SDLC pipeline, with runs per model, hand-offs, rework, a failed run, resumed sessions, closed tasks and verification gate checks, including rejections of every kind. The audit trail covers the same events, with agent moves and comments, people's moves and pull requests, and its hash chain is intact.

```sh
python3 tools/sample-metrics/generate.py                       # writes .symphony-metrics.jsonl and .symphony-audit.jsonl to tools/sample-metrics/ws/
dotnet run --project src/Symphony.Cli -- "$PWD/tools/sample-metrics/WORKFLOW.md"   # absolute: dotnet run starts in the project folder
# open http://127.0.0.1:4599/line and http://127.0.0.1:4599/audit
```

The data is deterministic for a given day (fixed seed, dates relative to now). The sample workflow points the tracker at a closed local port, so no real tracker is contacted; the log shows refused connections, which is expected. `ws/` is gitignored.
