#!/usr/bin/env python3
"""Compares a MapCue workspace explored on the demo app with expected-graph.md (STEP-05 demo).

    python3 -I samples/MapCue.DemoApp/compare-expected.py <workspace> <report.md>

Reads <workspace>/mapcue.db (SQLite) and the attempt artifacts under <workspace>/runs. Hard checks (exit code 1 when one fails):
  1. at least MIN_STATES states of the app were discovered;
  2. no risky action of expected-graph.md ("Akcje ryzykowne") was executed, and every executed action is safe or navigational;
  3. every observed edge has before evidence and after evidence (a state snapshot, or the screen outside the app);
  4. every discovered state can be named by a screen marker of expected-graph.md (screen.*) or is an AlertDialog.
The coverage of the expected transitions (profile basic, default policy) is reported, not enforced: it depends on budgets.
"""
import json
import re
import sqlite3
import sys
from pathlib import Path

APP = "com.mapcue.demoapp"
MIN_STATES = 3

# Transitions of expected-graph.md ("Przejścia") reachable by the basic profile under the default policy. Screens by marker,
# actions as "tap <AutomationId>", "tap row", "tap Navigate up" or "back"; "outside" = the app is left (launcher).
EXPECTED = [
    ("home", "tap home.accounts", "accounts"),
    ("home", "tap home.whatsnew", "whatsnew"),
    ("whatsnew", "tap whatsnew.close", "home"),
    ("whatsnew", "back", "home"),
    ("home", "tap tab.home", "home"),
    ("home", "tap tab.accounts", "accounts"),
    ("home", "tap tab.settings", "settings"),
    ("accounts", "tap tab.home", "home"),
    ("accounts", "tap tab.accounts", "accounts"),
    ("accounts", "tap tab.settings", "settings"),
    ("settings", "tap tab.home", "home"),
    ("settings", "tap tab.accounts", "accounts"),
    ("settings", "tap tab.settings", "settings"),
    ("accounts", "tap row", "details"),
    ("details", "tap details.home", "home"),
    ("details", "back", "accounts"),
    ("details", "tap Navigate up", "accounts"),
    ("settings", "tap settings.checkupdates", "verify"),
    ("settings", "tap settings.switchprofile", "profile"),
    ("verify", "back", "verify"),
    ("profile", "tap profile.basic", "home"),
    ("home", "back", "outside"),
    ("accounts", "back", "outside"),
    ("settings", "back", "outside"),
    ("profile", "back", "outside"),
]

# "Akcje ryzykowne" of expected-graph.md (plus the profile switch denied by mapcue.demo.yaml): never executed by default.
RISKY = ["details.delete", "investments.buy", "verify.sendcode", "verify.signout", "settings.notifications", "scenario.empty", "profile.investor"]
RISKY_TEXTS = ["Delete"]

MARKER = re.compile(r":id/screen\.([A-Za-z0-9_.-]+)$")


def state_names(db):
    names = {}
    for state_id, kind, tokens_json in db.execute("SELECT state_id, state_kind, identity_tokens_json FROM screen_states"):
        tokens = json.loads(tokens_json)
        markers = sorted({m.group(1) for t in tokens for m in [MARKER.search(t)] if m})
        if markers:
            name = "+".join(markers)
        elif "a:id:android:id/alertTitle" in tokens:
            name = "dialog"
        else:
            name = "?"
        names[state_id] = (name, kind, tokens)
    return names


def action_label(action_type, selector):
    if action_type == "Back":
        return "back"
    match = re.search(r"id:" + re.escape(APP) + r":id/([A-Za-z0-9_.-]+)", selector or "")
    if match:
        return "tap " + match.group(1)
    text = re.search(r"text:([^;]*)", selector or "")
    if text and "account" in text.group(1).lower() and "PLN" in text.group(1):
        return "tap row"
    if text:
        return "tap " + text.group(1)
    return f"{action_type.lower()} {selector}"


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    workspace, report = Path(sys.argv[1]), Path(sys.argv[2])
    db = sqlite3.connect(f"file:{workspace / 'mapcue.db'}?mode=ro", uri=True)
    names = state_names(db)
    candidates = {(s, k): (t, sel, risk) for s, k, t, sel, risk in db.execute(
        "SELECT state_id, action_key, action_type, canonical_selector, risk_class FROM action_candidates")}
    edges = list(db.execute("SELECT edge_id, from_state_id, action_key, to_state_id, status, observation_count FROM screen_edges"))
    observations = list(db.execute("SELECT attempt_id, edge_id, before_snapshot_id, after_snapshot_id, outcome FROM action_observations"))
    evidence = list(db.execute("SELECT kind, snapshot_id, attempt_id FROM evidence"))
    lines, failures = [], []

    # 1. states
    lines += ["## States", "", "| State | Screen | Kind |", "|---|---|---|"]
    for state_id, (name, kind, _) in sorted(names.items(), key=lambda kv: kv[1][0]):
        lines.append(f"| `{state_id}` | {name} | {kind} |")
    if len(names) < MIN_STATES:
        failures.append(f"only {len(names)} state(s) discovered (need at least {MIN_STATES})")
    unnamed = [s for s, (n, _, _) in names.items() if n == "?"]
    if unnamed:
        failures.append(f"states without a screen marker: {', '.join(unnamed)}")

    # 2. executed actions and policy
    executed = []
    for attempt_id, edge_id, *_ in observations:
        edge = next(e for e in edges if e[0] == edge_id)
        action_type, selector, risk = candidates[(edge[1], edge[2])]
        executed.append((names[edge[1]][0], action_label(action_type, selector), risk))
    for _, label, risk in executed:
        if any(label == "tap " + r for r in RISKY) or any(label == "tap " + t for t in RISKY_TEXTS):
            failures.append(f"risky action executed: {label}")
        if risk.lower() not in ("safe", "navigational"):
            failures.append(f"action executed with risk {risk}: {label}")
    skipped = sorted({(names[s][0], action_label(t, sel), risk) for (s, _), (t, sel, risk) in candidates.items()
                      if risk.lower() not in ("safe", "navigational")})
    lines += ["", "## Actions not executed by policy", "", "| Screen | Action | Risk |", "|---|---|---|"]
    lines += [f"| {s} | {a} | {r} |" for s, a, r in skipped]

    # 3. evidence
    for attempt_id, edge_id, before, after, outcome in observations:
        kinds = [(k, s, a) for k, s, a in evidence]
        if not any(k == "RawSnapshot" and s == before for k, s, _ in kinds):
            failures.append(f"attempt {attempt_id}: no before snapshot evidence")
        if after is not None:
            if not any(k == "RawSnapshot" and s == after for k, s, _ in kinds):
                failures.append(f"attempt {attempt_id}: no after snapshot evidence")
        elif outcome == "Succeeded" and not any(k == "RawSnapshot" and a == attempt_id for k, _, a in kinds):
            failures.append(f"attempt {attempt_id}: left the app without evidence of the screen outside it")

    # Transitions found vs expected
    found = set()
    lines += ["", "## Discovered transitions", "", "| From | Action | To | Edge status | Observations |", "|---|---|---|---|---|"]
    for edge_id, from_id, action_key, to_id, status, count in sorted(edges, key=lambda e: (names[e[1]][0], e[2])):
        action_type, selector, _ = candidates[(from_id, action_key)]
        label = action_label(action_type, selector)
        target = names[to_id][0] if to_id else ("outside" if status == "Executed" else "-")
        found.add((names[from_id][0], label, target))
        lines.append(f"| {names[from_id][0]} | {label} | {target} | {status} | {count} |")
    hits = [t for t in EXPECTED if t in found]
    lines += ["", f"## Expected transitions (expected-graph.md, basic, default policy): {len(hits)}/{len(EXPECTED)} found", "",
              "| From | Action | To | Found |", "|---|---|---|---|"]
    lines += [f"| {f} | {a} | {t} | {'yes' if (f, a, t) in found else 'no'} |" for f, a, t in EXPECTED]
    unexpected = sorted(found - set(EXPECTED))
    if unexpected:
        lines += ["", "## Transitions not in the expected list", ""] + [f"- {f} --{a}--> {t}" for f, a, t in unexpected]

    lines = ["# MapCue demo exploration vs expected-graph.md", "",
             f"- states: {len(names)}, edges: {len(edges)}, attempts: {len(observations)}",
             f"- hard checks: {'PASS' if not failures else 'FAIL'}"] + [f"  - {f}" for f in failures] + [""] + lines
    report.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
