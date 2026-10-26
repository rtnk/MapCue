#!/usr/bin/env python3
"""Consistency of the demo app's automation contract (STEP-02B).

Checks, without building the app:
  1. every id in AutomationIds.cs is used by a page and listed in expected-graph.md, and the list has no extra ids;
  2. every id-shaped selector in .tapcue/*.yaml exists in AutomationIds.cs;
  3. every flow targets the app's ApplicationId from the project file.
Usage: python3 -I samples/MapCue.DemoApp/check-consistency.py
"""
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
ID = re.compile(r"^[a-z]+\.[a-z]+$")

def main() -> int:
    errors = []
    source = (HERE / "AutomationIds.cs").read_text(encoding="utf-8")
    consts = dict(re.findall(r'public const string (\w+) = "([^"]+)";', source))
    ids = set(consts.values())
    if len(ids) != len(consts):
        errors.append("duplicate automation id values in AutomationIds.cs")
    for name, value in consts.items():
        if not ID.match(value):
            errors.append(f"{name}: '{value}' must be <lowercase>.<lowercase> (no digits: MapCue masks digits)")

    pages = "\n".join(p.read_text(encoding="utf-8") for p in sorted((HERE / "Pages").glob("*.cs")))
    for name in sorted(consts):
        if f"AutomationIds.{name}" not in pages:
            errors.append(f"AutomationIds.{name} is not used by any page")

    oracle = (HERE / "expected-graph.md").read_text(encoding="utf-8")
    listed_section = oracle.split("## Elementy z `AutomationId` (pełna lista)", 1)
    if len(listed_section) != 2:
        errors.append("expected-graph.md: section '## Elementy z `AutomationId` (pełna lista)' missing")
    else:
        listed = {t for t in re.findall(r"`([^`]+)`", listed_section[1]) if ID.match(t)}
        for missing in sorted(ids - listed):
            errors.append(f"expected-graph.md does not list '{missing}'")
        for extra in sorted(listed - ids):
            errors.append(f"expected-graph.md lists '{extra}', which is not in AutomationIds.cs")
    for token in sorted({t for t in re.findall(r"`([a-z]+\.[a-z]+)`", oracle)} - ids):
        if not token.endswith((".md", ".py", ".cs", ".yaml", ".yml", ".txt")):
            errors.append(f"expected-graph.md mentions unknown id '{token}'")

    app_id = re.search(r"<ApplicationId>([^<]+)</ApplicationId>", (HERE / "MapCue.DemoApp.csproj").read_text(encoding="utf-8")).group(1)
    flows = sorted((HERE / ".tapcue").glob("*.yaml"))
    if not flows:
        errors.append("no flows in .tapcue/")
    for flow in flows:
        text = flow.read_text(encoding="utf-8")
        if not re.search(rf"^appId: {re.escape(app_id)}$", text, re.M):
            errors.append(f"{flow.name}: appId is not {app_id}")
        for selector in re.findall(r"""(?:tapOn|assertVisible|assertNotVisible|visible):\s*["']([^"']+)["']""", text):
            if ID.match(selector) and selector not in ids:
                errors.append(f"{flow.name}: selector '{selector}' is not an automation id of the app")

    for error in errors:
        print(f"FAIL {error}")
    print(f"{'FAIL' if errors else 'PASS'}: {len(ids)} automation ids, {len(flows)} flows, appId {app_id}")
    return 1 if errors else 0

if __name__ == "__main__":
    sys.exit(main())
