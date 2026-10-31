#!/usr/bin/env python3
"""Compares a MapCue workspace explored on the demo app with expected-graph.md (STEP-05 demo, STEP-07 profiles).

    python3 -I samples/MapCue.DemoApp/compare-expected.py <workspace> <report.md>

Reads <workspace>/mapcue.db (SQLite) and the attempt artifacts under <workspace>/runs. Every exploration context (profile) is compared
on its own edges with the transitions of its profile. Hard checks (exit code 1 when one fails):
  1. at least MIN_STATES states of the app were discovered in each context;
  2. no risky action of expected-graph.md ("Akcje ryzykowne") was executed, nor a switch to another profile, and every executed action
     is safe or navigational;
  3. every observed edge has before evidence and after evidence (a state snapshot, or the screen outside the app), in its own context;
  4. every discovered state can be named by a screen marker of expected-graph.md (screen.*) or is an AlertDialog;
  5. (STEP-07) one state per screen marker (a profile difference never duplicates a state, R-04); with both profiles explored, the
     shared screens are states of both contexts, tab.investments is on the investor variants only and never an action of basic.
The coverage of the expected transitions (default policy) is reported, not enforced: it depends on budgets.
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
EXPECTED_BASIC = [
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

# The investor profile: the same transitions with its own profile button, plus the Investments tab (expected-graph.md, "tylko investor").
EXPECTED_INVESTOR = [t for t in EXPECTED_BASIC if t != ("profile", "tap profile.basic", "home")] + [
    ("profile", "tap profile.investor", "home"),
    ("home", "tap tab.investments", "investments"),
    ("accounts", "tap tab.investments", "investments"),
    ("settings", "tap tab.investments", "investments"),
    ("investments", "tap tab.home", "home"),
    ("investments", "tap tab.accounts", "accounts"),
    ("investments", "tap tab.investments", "investments"),
    ("investments", "tap tab.settings", "settings"),
    ("investments", "back", "outside"),
]

EXPECTED = {"basic": EXPECTED_BASIC, "investor": EXPECTED_INVESTOR}

# "Akcje ryzykowne" of expected-graph.md: never executed by default; a run never switches to another profile (policy.profileSwitch: deny).
RISKY = ["details.delete", "investments.buy", "verify.sendcode", "verify.signout", "settings.notifications", "scenario.empty"]
RISKY_TEXTS = ["Delete"]
SWITCH = {"basic": "profile.basic", "investor": "profile.investor"}
SHARED = ["accounts", "details", "home", "profile", "settings", "verify", "whatsnew"]
INVESTMENTS_TAB = "i:id:" + APP + ":id/tab.investments"

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


def compare_context(db, names, context_id, profile, candidates, evidence, snapshot_context):
    """Hard checks and the transition table of one context. Returns (lines, failures, found transitions)."""
    edges = list(db.execute("SELECT edge_id, from_state_id, action_key, to_state_id, status, observation_count FROM screen_edges WHERE context_id = ?", (context_id,)))
    observations = list(db.execute("SELECT attempt_id, edge_id, before_snapshot_id, after_snapshot_id, outcome FROM action_observations WHERE context_id = ?", (context_id,)))
    seen = sorted({s for (s,) in db.execute("SELECT state_id FROM screen_variants WHERE context_id = ?", (context_id,))})
    # Risk of a candidate in this context (candidate_assessments, STEP-07); the candidate row holds the first context's risk.
    assessed = {(st, k): risk for st, k, risk in db.execute("SELECT state_id, action_key, risk_class FROM candidate_assessments WHERE context_id = ?", (context_id,))}
    candidates = {key: (t, sel, assessed.get(key, risk)) for key, (t, sel, risk) in candidates.items()}
    lines, failures = [f"## Context `{context_id}` (profile {profile})", "",
                       f"- states: {len(seen)}, edges: {len(edges)}, attempts: {len(observations)}", ""], []
    if len(seen) < MIN_STATES:
        failures.append(f"{profile}: only {len(seen)} state(s) discovered (need at least {MIN_STATES})")

    # 2. executed actions and policy (another profile's button is a profile switch)
    risky = RISKY + [b for p, b in SWITCH.items() if p != profile]
    for attempt_id, edge_id, *_ in observations:
        edge = next(e for e in edges if e[0] == edge_id)
        action_type, selector, risk = candidates[(edge[1], edge[2])]
        label = action_label(action_type, selector)
        if any(label == "tap " + r for r in risky) or any(label == "tap " + t for t in RISKY_TEXTS):
            failures.append(f"{profile}: risky action executed: {label}")
        if risk.lower() not in ("safe", "navigational"):
            failures.append(f"{profile}: action executed with risk {risk}: {label}")

    # 3. evidence, in this context only
    for attempt_id, edge_id, before, after, outcome in observations:
        if snapshot_context.get(before) != context_id or (after is not None and snapshot_context.get(after) != context_id):
            failures.append(f"{profile}: attempt {attempt_id} uses a snapshot of another context")
        if not any(k == "RawSnapshot" and s == before for k, s, _, c in evidence if c == context_id):
            failures.append(f"{profile}: attempt {attempt_id}: no before snapshot evidence")
        if after is not None:
            if not any(k == "RawSnapshot" and s == after for k, s, _, c in evidence if c == context_id):
                failures.append(f"{profile}: attempt {attempt_id}: no after snapshot evidence")
        elif outcome == "Succeeded" and not any(k == "RawSnapshot" and a == attempt_id for k, _, a, c in evidence if c == context_id):
            failures.append(f"{profile}: attempt {attempt_id}: left the app without evidence of the screen outside it")

    found = set()
    lines += ["| From | Action | To | Edge status | Observations |", "|---|---|---|---|---|"]
    for edge_id, from_id, action_key, to_id, status, count in sorted(edges, key=lambda e: (names[e[1]][0], e[2])):
        action_type, selector, _ = candidates[(from_id, action_key)]
        label = action_label(action_type, selector)
        target = names[to_id][0] if to_id else ("outside" if status == "Executed" else "-")
        found.add((names[from_id][0], label, target))
        lines.append(f"| {names[from_id][0]} | {label} | {target} | {status} | {count} |")
        if profile == "basic" and label == "tap tab.investments":
            failures.append("basic: tab.investments is an action of the basic context")
    expected = EXPECTED.get(profile, [])
    hits = [t for t in expected if t in found]
    lines += ["", f"Expected transitions (expected-graph.md, {profile}, default policy): {len(hits)}/{len(expected)} found", "",
              "| From | Action | To | Found |", "|---|---|---|---|"]
    lines += [f"| {f} | {a} | {t} | {'yes' if (f, a, t) in found else 'no'} |" for f, a, t in expected]
    unexpected = sorted(found - set(expected))
    if unexpected:
        lines += ["", "Transitions not in the expected list:", ""] + [f"- {f} --{a}--> {t}" for f, a, t in unexpected]
    return lines + [""], failures, seen


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    workspace, report = Path(sys.argv[1]), Path(sys.argv[2])
    db = sqlite3.connect(f"file:{workspace / 'mapcue.db'}?mode=ro", uri=True)
    names = state_names(db)
    candidates = {(s, k): (t, sel, risk) for s, k, t, sel, risk in db.execute(
        "SELECT state_id, action_key, action_type, canonical_selector, risk_class FROM action_candidates")}
    evidence = list(db.execute("SELECT kind, snapshot_id, attempt_id, context_id FROM evidence"))
    snapshot_context = dict(db.execute("SELECT snapshot_id, context_id FROM ui_snapshots"))
    contexts = list(db.execute("SELECT context_id, profile_id FROM contexts ORDER BY profile_id, context_id"))
    lines, failures = [], []

    # 1. and 4. states
    lines += ["## States", "", "| State | Screen | Kind | Contexts |", "|---|---|---|---|"]
    variants = {}
    for state_id, context_id, tokens_json in db.execute("SELECT state_id, context_id, identity_tokens_json FROM screen_variants"):
        variants.setdefault(state_id, {})[context_id] = json.loads(tokens_json)
    profile_of = dict(contexts)
    for state_id, (name, kind, _) in sorted(names.items(), key=lambda kv: kv[1][0]):
        seen_in = ", ".join(sorted(profile_of[c] for c in variants.get(state_id, {})))
        lines.append(f"| `{state_id}` | {name} | {kind} | {seen_in} |")
    unnamed = [s for s, (n, _, _) in names.items() if n == "?"]
    if unnamed:
        failures.append(f"states without a screen marker: {', '.join(unnamed)}")

    # 5. one state per screen marker; profiles share the common screens; the Investments tab only on investor variants
    by_name = {}
    for state_id, (name, _, _) in names.items():
        by_name.setdefault(name, []).append(state_id)
    for name, ids in sorted(by_name.items()):
        if name not in ("dialog", "?") and len(ids) > 1:
            failures.append(f"screen {name} has {len(ids)} states (duplicate from a profile or data difference, R-04): {', '.join(sorted(ids))}")
            # Diagnostics for R-04 calibration: identity tokens each duplicate has that the first one lacks (and the reverse).
            first = sorted(ids)[0]
            for other in sorted(ids)[1:]:
                a, b = names[first][2], names[other][2]
                only_a = sorted(set(a) - set(b))
                only_b = sorted(set(b) - set(a))
                lines.append(f"- duplicate {name}: `{first}` ({len(a)} tokens) vs `{other}` ({len(b)} tokens)")
                lines += [f"  - only `{first}`: `{t}`" for t in only_a] + [f"  - only `{other}`: `{t}`" for t in only_b]
    profiles = {p: c for c, p in contexts}
    for state_id, per_context in variants.items():
        for context_id, tokens in per_context.items():
            if profile_of[context_id] == "basic" and INVESTMENTS_TAB in tokens:
                failures.append(f"basic variant of {names[state_id][0]} has tab.investments")
    if "basic" in profiles and "investor" in profiles:
        for name in SHARED:
            ids = by_name.get(name, [])
            if ids and not all(c in variants.get(ids[0], {}) for c in (profiles["basic"], profiles["investor"])):
                lines.append(f"- note: shared screen {name} was not seen in both profiles")
        home = by_name.get("home", [])
        if home and INVESTMENTS_TAB not in variants.get(home[0], {}).get(profiles["investor"], []):
            failures.append("the investor variant of home has no tab.investments")
    lines.append("")

    found_states = set()
    for context_id, profile in contexts:
        ctx_lines, ctx_failures, seen = compare_context(db, names, context_id, profile, candidates, evidence, snapshot_context)
        lines += ctx_lines
        failures += ctx_failures
        found_states.update(seen)

    skipped = sorted({(profile_of[c], names[s][0], action_label(*candidates[(s, k)][:2]), risk)
                      for s, k, c, risk in db.execute("SELECT state_id, action_key, context_id, risk_class FROM candidate_assessments")
                      if risk.lower() not in ("safe", "navigational")})
    lines += ["## Actions not executed by policy", "", "| Profile | Screen | Action | Risk |", "|---|---|---|---|"]
    lines += [f"| {p} | {s} | {a} | {r} |" for p, s, a, r in skipped]

    lines = ["# MapCue demo exploration vs expected-graph.md", "",
             f"- contexts: {', '.join(p for _, p in contexts)}; states: {len(names)}",
             f"- hard checks: {'PASS' if not failures else 'FAIL'}"] + [f"  - {f}" for f in failures] + [""] + lines
    report.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
