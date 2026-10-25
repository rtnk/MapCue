#!/usr/bin/env python3
"""STEP-00 spike: talk to `tapcue mcp` over STDIO (JSON-RPC, one message per line).

Not production code. It records what TapCue 0.1.0 really exposes, so STEP-02 can write
contract tests against observed behaviour instead of assumptions.

Usage:
  python3 -I mcp_smoke.py --tapcue /path/to/bin/tapcue                 # no device needed
  python3 -I mcp_smoke.py --tapcue /path/to/bin/tapcue \
      --device emulator-5554 --app-id com.android.settings --out ./out  # needs a booted device

With --device the script also calls inspect_screen, take_screenshot and run (launchApp +
one safe action: `back`), and writes the raw responses to --out.
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import time


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tapcue", required=True)
    ap.add_argument("--device")
    ap.add_argument("--app-id")
    ap.add_argument("--out", default="mcp-smoke-out")
    args = ap.parse_args()

    proc = subprocess.Popen(
        [args.tapcue, "mcp"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
    )
    next_id = [0]

    def call(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            next_id[0] += 1
            msg["id"] = next_id[0]
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()
        if notify:
            return None
        started = time.time()
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError(f"no response to {method}")
        resp = json.loads(line)
        resp["_elapsed_s"] = round(time.time() - started, 2)
        return resp

    def tool(name, arguments):
        return call("tools/call", {"name": name, "arguments": arguments})

    init = call("initialize", {
        "protocolVersion": "2025-06-18",
        "capabilities": {},
        "clientInfo": {"name": "mapcue-step00-smoke", "version": "0"},
    })
    res = init["result"]
    print("initialize:", json.dumps(res["serverInfo"]), "protocol:", res.get("protocolVersion"),
          "capabilities:", json.dumps(res.get("capabilities")))
    call("notifications/initialized", notify=True)

    tools = call("tools/list")["result"]["tools"]
    for t in tools:
        schema = t["inputSchema"]
        print("tool:", t["name"], "args:", sorted(schema.get("properties", {}).keys()),
              "required:", schema.get("required"))

    print("list_devices:", json.dumps(tool("list_devices", {})["result"])[:400])

    probe_device = args.device or "emulator-5554"
    r = tool("inspect_screen", {"device_id": probe_device})
    print(f"inspect_screen({probe_device}): isError={r['result'].get('isError', False)} "
          f"elapsed={r['_elapsed_s']}s text={r['result']['content'][0].get('text', '')[:200]!r}")

    r = tool("run", {"device_id": probe_device, "yaml": "- back", "files": ["a.yaml"]})
    print("run(yaml+files):", json.dumps(r["result"])[:300])

    if args.device:
        os.makedirs(args.out, exist_ok=True)
        if args.app_id:
            r = tool("run", {"device_id": args.device, "yaml": f"appId: {args.app_id}\n---\n- launchApp\n"})
            print("run(launchApp):", r["_elapsed_s"], "s", json.dumps(r["result"])[:300])
        r = tool("inspect_screen", {"device_id": args.device})
        with open(os.path.join(args.out, "inspect_screen.json"), "w") as f:
            f.write(r["result"]["content"][0]["text"])
        print("inspect_screen:", r["_elapsed_s"], "s ->", os.path.join(args.out, "inspect_screen.json"))
        r = tool("take_screenshot", {"device_id": args.device})
        content = r["result"]["content"][0]
        if content.get("type") == "image":
            with open(os.path.join(args.out, "screenshot.jpg"), "wb") as f:
                f.write(base64.b64decode(content["data"]))
        print("take_screenshot:", r["_elapsed_s"], "s mime:", content.get("mimeType"))
        r = tool("run", {"device_id": args.device, "yaml": "- back\n"})
        print("run(back):", r["_elapsed_s"], "s", json.dumps(r["result"])[:300])
        r = tool("inspect_screen", {"device_id": args.device})
        with open(os.path.join(args.out, "inspect_screen_after.json"), "w") as f:
            f.write(r["result"]["content"][0]["text"])
        print("inspect_screen(after):", r["_elapsed_s"], "s")

    proc.stdin.close()
    proc.wait(timeout=60)
    print("exit:", proc.returncode)
    return 0


if __name__ == "__main__":
    sys.exit(main())
