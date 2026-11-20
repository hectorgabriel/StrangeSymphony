#!/usr/bin/env python3
"""Writes a sample .symphony-metrics.jsonl and .symphony-audit.jsonl for the SDLC pipeline: 14 tasks over the
last 13 days, with runs, hand-offs, rework, failed runs, closed tasks, verification gate checks, rework loops (the
tester's defect reports before Con Defectos, and a reviewer's send-back without one), and the audit trail of the same
events (agent moves and comments, people's moves, a pull request).

Usage: generate.py [workspace-root]   (writes both files to <workspace-root>; default: ws/ next to this script)
Deterministic: the same seed and date give the same file.
"""
import hashlib
import json
import os
import random
import sys
import uuid
from datetime import datetime, timedelta, timezone

random.seed(7)
NOW = datetime.now(timezone.utc).replace(microsecond=0)
MODELS = {"Por Analizar": "opus", "Por Desarrollar": "opus", "Plan Aprobado": "sonnet",
          "Por Probar": "sonnet", "Por Documentar": "qwen3.8-27b-deep"}
records = []
audit = []


def iso(t):
    return t.isoformat().replace("+00:00", "Z")


def rec(**kw):
    records.append({k: v for k, v in kw.items() if v not in (None, False, 0)})


def aud(task, kind, actor, **kw):
    audit.append({"kind": kind, "actor": actor, "issue_id": task.id, "identifier": task.identifier, "at": iso(task.t),
                  **{k: v for k, v in kw.items() if v not in (None, False, 0)}})


def fingerprint(text):
    return {"chars": len(text), "sha256": hashlib.sha256(text.encode()).hexdigest()[:16]}


class Task:
    def __init__(self, n, start):
        self.id = f"IEAB{n:04d}"
        self.identifier = f"T{n:02d}"
        self.t = start
        self.handed_off = set()
        self.last_run_state = None
        self.state = None  # what the tracker shows, for the audit trail
        self.run_id = None
        self.session = None
        self.branch = f"feature/{self.identifier.lower()}"

    def move_outside(self, to_state):
        """A person moved the task in the tracker."""
        if self.state and self.state != to_state:
            aud(self, "state_changed", "outside", state=to_state, from_state=self.state)
        self.state = to_state

    def run(self, state, minutes, outcome="completed", turns=None, resumed=False, error=None, model=None):
        model = model or MODELS[state]
        seconds = minutes * 60 * random.uniform(0.8, 1.25)
        started = self.t
        self.flush()
        self.move_outside(state)
        self.run_id = str(uuid.UUID(int=random.getrandbits(128)))
        if not resumed or self.session is None:
            self.session = str(uuid.UUID(int=random.getrandbits(128)))
        aud(self, "dispatched", "symphony", state=state, run_id=self.run_id, session_id=self.session if resumed else None,
            model=model, attempt=1 if outcome != "completed" else None, resumed=resumed)
        self.t += timedelta(seconds=seconds)
        scale = {"opus": 1.3, "sonnet": 1.0, "qwen3.8-27b-deep": 0.6}[model]
        inp = int(random.uniform(1.5e6, 6e6) * minutes / 20 * scale)
        out = int(random.uniform(12e3, 45e3) * minutes / 20)
        rec(kind="run", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state=state, model=model,
            started_at=iso(started), seconds=round(seconds, 1), turns=turns or random.randint(2, 9),
            input_tokens=inp, output_tokens=out, total_tokens=inp + out, attempt=None if outcome == "completed" else 1,
            outcome=outcome, error=error, resumed=resumed, rework=state in self.handed_off)
        self.last_run_state = state
        # The session ends after its last tool call (the gate below), so the audit line waits for flush().
        self.pending = dict(state=state, run_id=self.run_id, session_id=self.session, model=model, resumed=resumed, outcome=outcome,
                            error=error, turns=records[-1]["turns"], total_tokens=inp + out, seconds=round(seconds, 1))

    def flush(self):
        if getattr(self, "pending", None):
            self.t += timedelta(seconds=5)
            aud(self, "run_ended", "symphony", **self.pending)
            self.pending = None

    def gate(self, state, outcome, seconds):
        self.t += timedelta(seconds=seconds)
        rec(kind="gate", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state=state,
            seconds=seconds, outcome=outcome, from_state=self.last_run_state)
        ok = outcome == "passed"
        common = dict(run_id=self.run_id, session_id=self.session, model=MODELS.get(self.last_run_state), tool="update_state")
        if ok and state == "Por Probar":
            title = f"{self.identifier}: cambios del plan aprobado"
            aud(self, "tool_call", "agent", **common | {"tool": "create_pull_request"}, args={"title": fingerprint(title)}, ok=True,
                url=f"https://dev.azure.com/acme/sdlc/_git/app/pullrequest/{int(self.id[4:]) + 100}")
        aud(self, "tool_call", "agent", state=state, from_state=self.state, **common, args={"state": state}, ok=ok,
            category=None if ok else outcome, commit=hashlib.sha1(f"{self.id}{self.t}".encode()).hexdigest() if ok else None,
            branch=self.branch if ok else None)
        if ok:
            note = f"Listo para {state}: resumen del trabajo en {self.identifier}."
            aud(self, "tool_call", "agent", **common | {"tool": "add_comment"}, args={"body": fingerprint(note)}, ok=True)
            self.state = state
            self.agent_moved = True

    def send_back(self, defects):
        """The tester reports its defects (category -> severity -> count), then moves the task to Con Defectos."""
        self.t += timedelta(minutes=2)
        counts = {}
        for (category, severity), n in defects.items():
            counts.setdefault(category, {})[severity] = n
        total = lambda key: {k: sum(n for (c, s), n in defects.items() if (c if key == "c" else s) == k)
                             for k in dict.fromkeys(c if key == "c" else s for c, s in defects)}
        aud(self, "tool_call", "agent", run_id=self.run_id, session_id=self.session, model=MODELS["Por Probar"], tool="report_defects",
            args={"categories": total("c"), "severities": total("s")}, ok=True)
        self.gate("Con Defectos", "passed", 3)
        rec(kind="defects", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state="Con Defectos",
            model=MODELS["Por Probar"], from_state="Por Probar", defect_counts=counts)

    def handoff(self, to_state, wait_hours, closed=False):
        if self.last_run_state and self.last_run_state != to_state:
            self.handed_off.add(self.last_run_state)
        self.flush()
        self.t += timedelta(minutes=random.randint(1, 4))
        rec(kind="closed" if closed else "handoff", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state=to_state)
        if getattr(self, "agent_moved", False):
            # Symphony lets the task go once the agent's own move takes it out of the active statuses.
            aud(self, "released", "symphony", state=self.state, reason=f'Moved to "{self.state}"')
            self.agent_moved = False
        self.move_outside(to_state)
        if closed:
            aud(self, "closed", "symphony", state=to_state, reason=f'Reached terminal state "{to_state}"')
        self.t += timedelta(hours=wait_hours * random.uniform(0.6, 1.4))

    def stage(self, state, minutes, gate_state, rejections=(), gate_seconds=20, to=None, wait=3.0):
        """One agent stage: the run, the gate's rejected checks (each followed by a short fix), the pass, the hand-off."""
        self.run(state, minutes)
        for category in rejections:
            self.gate(gate_state, category, gate_seconds if category == "verification_failed" else 2)
            self.t += timedelta(minutes=random.randint(3, 15))
        self.gate(gate_state, "passed", gate_seconds)
        self.handoff(to or gate_state, wait)


def build(n, start, *, plan_rejections=(), impl_rejections=(), test_rejections=(), defects=(),
          stop_after=None, failed_run=False, resumed=False, impl_model=None, review_send_back=False):
    t = Task(n, start)
    t.stage("Por Analizar", 25, "Analizado", to="Por Desarrollar", wait=6)
    if stop_after == "analysis":
        return t.flush()
    t.stage("Por Desarrollar", 15, "Plan Propuesto", rejections=plan_rejections, to="Plan Aprobado", wait=4)
    if failed_run:
        t.run("Plan Aprobado", 30, outcome="failed", error="agent turn error: turn_timeout: turn exceeded 3600000 ms")
        t.t += timedelta(minutes=5)
    t.run("Plan Aprobado", 45, resumed=resumed or failed_run, model=impl_model)
    for category in impl_rejections:
        t.gate("Por Probar", category, 240 if category == "verification_failed" else 2)
        t.t += timedelta(minutes=random.randint(5, 25))
    if stop_after == "implementation" and impl_rejections and impl_rejections[-1] != "passed":
        return t.flush()  # still stuck at the gate
    t.gate("Por Probar", "passed", 260)
    t.handoff("Por Probar", 0.02)
    if stop_after == "implementation":
        return t.flush()
    t.run("Por Probar", 35)
    for i, found in enumerate(defects):
        t.send_back(found)
        if stop_after == "defects" and i == len(defects) - 1:
            return t.handoff("Con Defectos", 0) or t.flush()  # nobody has triaged it yet
        t.handoff("Con Defectos", 5)
        t.handoff("Plan Aprobado", 0.5)
        t.run("Plan Aprobado", 20, model=impl_model)  # rework: the stage was handed off before
        t.gate("Por Probar", "passed", 250)
        t.handoff("Por Probar", 0.02)
        t.run("Por Probar", 20)  # rework too
    for category in test_rejections:
        t.gate("En Revision", category, 280 if category == "verification_failed" else 2)
        t.t += timedelta(minutes=random.randint(5, 20))
    t.gate("En Revision", "passed", 290)
    t.handoff("En Revision", 18)
    if review_send_back:
        # The reviewer sends it back from the pull request: rework without a defect report.
        t.handoff("Plan Aprobado", 0.3)
        t.run("Plan Aprobado", 15, model=impl_model)
        t.gate("Por Probar", "passed", 240)
        t.handoff("Por Probar", 0.02)
        t.run("Por Probar", 15)
        t.gate("En Revision", "passed", 280)
        t.handoff("En Revision", 8)
    if stop_after == "review":
        return t.flush()
    t.handoff("Por Documentar", 0.3)
    t.stage("Por Documentar", 20, "Documentado", to="Documentado", wait=10)
    if t.t < NOW:
        t.handoff("Completado", 0, closed=True)
    t.flush()


day = lambda d, h=9: (NOW - timedelta(days=d)).replace(hour=h, minute=random.randint(0, 59), second=0)
build(1, day(13))
build(2, day(13, 11), impl_rejections=["verification_failed"])
build(3, day(12), plan_rejections=["uncommitted_changes"], defects=[{("acceptance_criteria", "major"): 2, ("validation", "minor"): 1}])
build(4, day(11), impl_rejections=["not_pushed", "verification_failed"], test_rejections=["verification_failed"])
build(5, day(11, 14), failed_run=True)
build(6, day(10), impl_rejections=["verification_failed", "verification_failed"], impl_model="opus",
      defects=[{("business_rule", "critical"): 1, ("data", "major"): 1}, {("business_rule", "major"): 1}])
build(7, day(9), test_rejections=["uncommitted_changes"], review_send_back=True)
build(8, day(8), resumed=True)
build(9, day(7), impl_rejections=["not_pushed"], defects=[{("ui", "minor"): 3, ("acceptance_criteria", "critical"): 1}], stop_after="review")
build(10, day(5), impl_rejections=["verification_failed"], stop_after="review")
build(11, day(4), plan_rejections=["not_pushed"], stop_after="implementation", impl_rejections=["verification_failed"])
build(12, day(3), impl_rejections=["verification_failed", "workspace_changed", "verification_failed"], stop_after="implementation")
build(13, day(2), stop_after="analysis")
build(14, day(1), impl_rejections=["uncommitted_changes"], stop_after="implementation")
build(15, day(3, 8), defects=[{("regression", "critical"): 1, ("validation", "major"): 2}], stop_after="defects")

records = [r for r in records if r["at"] <= iso(NOW)]
records.sort(key=lambda r: r["at"])
root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "ws")
os.makedirs(root, exist_ok=True)
path = os.path.join(root, ".symphony-metrics.jsonl")
with open(path, "w") as f:
    for r in records:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")

# The audit trail: the same chain Symphony writes. Each line has seq and prev (the hash of the line before), and ends
# with hash, the SHA-256 of the line without it.
audit = sorted((a for a in audit if a["at"] <= iso(NOW)), key=lambda a: a["at"])
prev = "0" * 64
audit_path = os.path.join(root, ".symphony-audit.jsonl")
with open(audit_path, "w") as f:
    for seq, a in enumerate(audit, 1):
        body = json.dumps(a | {"seq": seq, "prev": prev}, ensure_ascii=False, separators=(",", ":"))
        prev = hashlib.sha256(body.encode()).hexdigest()
        f.write(body[:-1] + ',"hash":"' + prev + '"}\n')
print(f"{audit_path}: {len(audit)} lines")
kinds = {}
for r in records:
    kinds[r["kind"]] = kinds.get(r["kind"], 0) + 1
print(f"{path}: {len(records)} lines {kinds}")
