# Sample metrics for `/line`

Made-up metrics for checking the Metrics page (`/line`) and `GET /api/v1/metrics` without running real agents: 14 tasks over the last 13 days of the SDLC pipeline, with runs per model, hand-offs, rework, a failed run, resumed sessions, closed tasks and verification gate checks, including rejections of every kind.

```sh
python3 tools/sample-metrics/generate.py                       # writes tools/sample-metrics/ws/.symphony-metrics.jsonl
dotnet run --project src/Symphony.Cli -- "$PWD/tools/sample-metrics/WORKFLOW.md"   # absolute: dotnet run starts in the project folder
# open http://127.0.0.1:4599/line
```

The data is deterministic for a given day (fixed seed, dates relative to now). The sample workflow points the tracker at a closed local port, so no real tracker is contacted; the log shows refused connections, which is expected. `ws/` is gitignored.
