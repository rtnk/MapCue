#!/usr/bin/env python3
"""Records a real `tapcue mcp` STDIO session as a JSONL fixture for MapCue contract tests.

Each output line is {"dir": "c2s" | "s2c", "msg": <JSON-RPC message>}. The fixture is replayed by
FakeTapCueServer (tests/MapCue.Adapters.TapCue.FakeServer) and parsed by the contract tests.

Usage (no device needed for the default script; pass --device for device scenarios):
  python3 -I record_mcp_fixture.py --tapcue /path/to/bin/tapcue --out mcp-no-device.jsonl
  python3 -I record_mcp_fixture.py --tapcue ... --device emulator-5554 --app-id com.android.settings --out mcp-android.jsonl

The process is started with a minimal environment (no MAESTRO_*), the same way the adapter starts it.
"""
import argparse
import json
import os
import subprocess
import sys

KEEP_ENV = ("PATH", "HOME", "JAVA_HOME", "ANDROID_HOME", "ANDROID_SDK_ROOT", "TMPDIR", "LANG", "XDG_STATE_HOME")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tapcue", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--device")
    ap.add_argument("--app-id")
    ap.add_argument("--safe-command", default="back", help="single safe command for the device scenario (back or scroll)")
    args = ap.parse_args()

    env = {k: v for k, v in os.environ.items() if k in KEEP_ENV}
    proc = subprocess.Popen([args.tapcue, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, text=True, env=env)
    lines = []
    next_id = [0]

    def send(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if not notify:
            next_id[0] += 1
            msg["id"] = next_id[0]
        if params is not None:
            msg["params"] = params
        lines.append({"dir": "c2s", "msg": msg})
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()
        if notify:
            return None
        resp = json.loads(proc.stdout.readline())
        lines.append({"dir": "s2c", "msg": resp})
        return resp

    def tool(name, arguments):
        return send("tools/call", {"name": name, "arguments": arguments})

    send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                        "clientInfo": {"name": "mapcue", "version": "0.1.0"}})
    send("notifications/initialized", notify=True)
    send("tools/list")
    tool("list_devices", {})
    if args.device:
        if args.app_id:
            tool("run", {"device_id": args.device, "yaml": f"appId: {args.app_id}\n---\n- launchApp\n"})
        tool("inspect_screen", {"device_id": args.device})
        tool("run", {"device_id": args.device, "yaml": f"appId: {args.app_id or 'com.android.settings'}\n---\n- {args.safe_command}\n"})
        tool("inspect_screen", {"device_id": args.device})
    else:
        tool("inspect_screen", {"device_id": "emulator-5554"})
        tool("run", {"device_id": "emulator-5554", "yaml": "- back\n", "files": ["a.yaml"]})
        tool("run", {"device_id": "emulator-5554", "yaml": "appId: com.example\n---\n- back\n"})
        tool("run", {"device_id": "emulator-5554"})
        send("tools/call", {"name": "no_such_tool", "arguments": {}})
    proc.stdin.close()
    proc.wait(timeout=60)
    with open(args.out, "w", encoding="utf-8") as f:
        for line in lines:
            f.write(json.dumps(line, sort_keys=True, ensure_ascii=False) + "\n")
    print(f"wrote {len(lines)} lines to {args.out}, tapcue exit {proc.returncode}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
