#!/usr/bin/env python3
"""Writes a sample .symphony-metrics.jsonl for the SDLC pipeline: 14 tasks over the last 13 days,
with runs, hand-offs, rework, failed runs, closed tasks and verification gate checks.

Usage: generate.py [workspace-root]   (writes <workspace-root>/.symphony-metrics.jsonl; default: ws/ next to this script)
Deterministic: the same seed and date give the same file.
"""
import json
import os
import random
import sys
from datetime import datetime, timedelta, timezone

random.seed(7)
NOW = datetime.now(timezone.utc).replace(microsecond=0)
MODELS = {"Por Analizar": "opus", "Por Desarrollar": "opus", "Plan Aprobado": "sonnet",
          "Por Probar": "sonnet", "Por Documentar": "qwen3.8-27b-deep"}
records = []


def iso(t):
    return t.isoformat().replace("+00:00", "Z")


def rec(**kw):
    records.append({k: v for k, v in kw.items() if v not in (None, False, 0)})


class Task:
    def __init__(self, n, start):
        self.id = f"IEAB{n:04d}"
        self.identifier = f"T{n:02d}"
        self.t = start
        self.handed_off = set()
        self.last_run_state = None

    def run(self, state, minutes, outcome="completed", turns=None, resumed=False, error=None):
        model = MODELS[state]
        seconds = minutes * 60 * random.uniform(0.8, 1.25)
        started = self.t
        self.t += timedelta(seconds=seconds)
        scale = {"opus": 1.3, "sonnet": 1.0, "qwen3.8-27b-deep": 0.6}[model]
        inp = int(random.uniform(1.5e6, 6e6) * minutes / 20 * scale)
        out = int(random.uniform(12e3, 45e3) * minutes / 20)
        rec(kind="run", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state=state, model=model,
            started_at=iso(started), seconds=round(seconds, 1), turns=turns or random.randint(2, 9),
            input_tokens=inp, output_tokens=out, total_tokens=inp + out, attempt=None if outcome == "completed" else 1,
            outcome=outcome, error=error, resumed=resumed, rework=state in self.handed_off)
        self.last_run_state = state

    def gate(self, state, outcome, seconds):
        self.t += timedelta(seconds=seconds)
        rec(kind="gate", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state=state,
            seconds=seconds, outcome=outcome, from_state=self.last_run_state)

    def handoff(self, to_state, wait_hours, closed=False):
        if self.last_run_state and self.last_run_state != to_state:
            self.handed_off.add(self.last_run_state)
        self.t += timedelta(minutes=random.randint(1, 4))
        rec(kind="closed" if closed else "handoff", issue_id=self.id, identifier=self.identifier, at=iso(self.t), state=to_state)
        self.t += timedelta(hours=wait_hours * random.uniform(0.6, 1.4))

    def stage(self, state, minutes, gate_state, rejections=(), gate_seconds=20, to=None, wait=3.0):
        """One agent stage: the run, the gate's rejected checks (each followed by a short fix), the pass, the hand-off."""
        self.run(state, minutes)
        for category in rejections:
            self.gate(gate_state, category, gate_seconds if category == "verification_failed" else 2)
            self.t += timedelta(minutes=random.randint(3, 15))
        self.gate(gate_state, "passed", gate_seconds)
        self.handoff(to or gate_state, wait)


def build(n, start, *, plan_rejections=(), impl_rejections=(), test_rejections=(), defects=False,
          stop_after=None, failed_run=False, resumed=False):
    t = Task(n, start)
    t.stage("Por Analizar", 25, "Analizado", to="Por Desarrollar", wait=6)
    if stop_after == "analysis":
        return
    t.stage("Por Desarrollar", 15, "Plan Propuesto", rejections=plan_rejections, to="Plan Aprobado", wait=4)
    if failed_run:
        t.run("Plan Aprobado", 30, outcome="failed", error="agent turn error: turn_timeout: turn exceeded 3600000 ms")
        t.t += timedelta(minutes=5)
    t.run("Plan Aprobado", 45, resumed=resumed or failed_run)
    for category in impl_rejections:
        t.gate("Por Probar", category, 240 if category == "verification_failed" else 2)
        t.t += timedelta(minutes=random.randint(5, 25))
    if stop_after == "implementation" and impl_rejections and impl_rejections[-1] != "passed":
        return  # still stuck at the gate
    t.gate("Por Probar", "passed", 260)
    t.handoff("Por Probar", 0.02)
    if stop_after == "implementation":
        return
    t.run("Por Probar", 35)
    if defects:
        t.gate("Con Defectos", "passed", 3)
        t.handoff("Con Defectos", 5)
        t.handoff("Plan Aprobado", 0.5)
        t.run("Plan Aprobado", 20)  # rework: the stage was handed off before
        t.gate("Por Probar", "passed", 250)
        t.handoff("Por Probar", 0.02)
        t.run("Por Probar", 20)  # rework too
    for category in test_rejections:
        t.gate("En Revision", category, 280 if category == "verification_failed" else 2)
        t.t += timedelta(minutes=random.randint(5, 20))
    t.gate("En Revision", "passed", 290)
    t.handoff("En Revision", 18)
    if stop_after == "review":
        return
    t.handoff("Por Documentar", 0.3)
    t.stage("Por Documentar", 20, "Documentado", to="Documentado", wait=10)
    if t.t < NOW:
        t.handoff("Completado", 0, closed=True)


day = lambda d, h=9: (NOW - timedelta(days=d)).replace(hour=h, minute=random.randint(0, 59), second=0)
build(1, day(13))
build(2, day(13, 11), impl_rejections=["verification_failed"])
build(3, day(12), plan_rejections=["uncommitted_changes"], defects=True)
build(4, day(11), impl_rejections=["not_pushed", "verification_failed"], test_rejections=["verification_failed"])
build(5, day(11, 14), failed_run=True)
build(6, day(10), impl_rejections=["verification_failed", "verification_failed"], defects=True)
build(7, day(9), test_rejections=["uncommitted_changes"])
build(8, day(8), resumed=True)
build(9, day(7), impl_rejections=["not_pushed"], defects=True, stop_after="review")
build(10, day(5), impl_rejections=["verification_failed"], stop_after="review")
build(11, day(4), plan_rejections=["not_pushed"], stop_after="implementation", impl_rejections=["verification_failed"])
build(12, day(3), impl_rejections=["verification_failed", "workspace_changed", "verification_failed"], stop_after="implementation")
build(13, day(2), stop_after="analysis")
build(14, day(1), impl_rejections=["uncommitted_changes"], stop_after="implementation")

records = [r for r in records if r["at"] <= iso(NOW)]
records.sort(key=lambda r: r["at"])
root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "ws")
os.makedirs(root, exist_ok=True)
path = os.path.join(root, ".symphony-metrics.jsonl")
with open(path, "w") as f:
    for r in records:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
kinds = {}
for r in records:
    kinds[r["kind"]] = kinds.get(r["kind"], 0) + 1
print(f"{path}: {len(records)} lines {kinds}")
