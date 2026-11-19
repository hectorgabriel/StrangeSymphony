---
# Sample: serves the generated metrics on /line. The tracker URL is a closed local port, so no real tracker is contacted.
tracker:
  kind: azure_devops
  provider:
    organization_url: https://127.0.0.1:9
    project: Sample
    token: sample-not-a-token
  active_states: [Por Analizar, Por Desarrollar, Plan Aprobado, Por Probar, Por Documentar]
  terminal_states: [Completado, Cancelado]
polling:
  interval_ms: 3600000
workspace:
  root: ws                          # relative to this file; generate.py writes here
server:
  port: 4599
---
Sample only.
