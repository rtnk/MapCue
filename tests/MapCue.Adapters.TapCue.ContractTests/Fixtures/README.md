# TapCue fixtures (contract tests)

One directory per supported TapCue version (`tapcue-<version>/`). A new TapCue version needs new fixtures and a passing
smoke before it is added to `tapcue.supportedVersions` (docs/tapcue-compatibility.md §8, R-12).

Transcripts are JSONL: one `{"dir": "c2s" | "s2c", "msg": <JSON-RPC message>}` per line. They are parsed by the
contract tests and replayed by the fake server (`tests/MapCue.Adapters.TapCue.FakeServer`) in integration tests.

| File | Provenance |
|---|---|
| `tapcue-0.1.0/mcp-no-device.jsonl` | **Recorded** from the real `tapcue mcp` 0.1.0 (release archive, SHA-256 `e8dd01b0…6ec1`) on Linux x86_64 without a device, with `record_mcp_fixture.py`. CI re-records it in `tapcue-smoke.yml` (`tapcue-cli` job) and fails on drift. |
| `tapcue-0.1.0/mcp-android-settings.jsonl` | **Recorded** on the Android emulator (API 34 `google_apis` x86_64, GitHub Actions ubuntu-24.04, `tapcue-smoke.yml` run 40239216271, commit `defec15`): `list_devices` → `run launchApp` (Settings) → `inspect_screen` → `run back` → `inspect_screen`. Copied from the job log between `=== BEGIN/END` markers (artifact storage is not reachable from the agent sandbox). |
| `tapcue-0.1.0/hierarchy-android-launcher.txt` | **Recorded** in the same run: stdout of `tapcue --device emulator-5554 hierarchy --no-reinstall-driver` (launcher screen after `back`). |
| `tapcue-0.1.0/mcp-android-settings.synthetic.jsonl` | **Synthetic**, kept for targeted assertions: handshake and `tools/list` copied from the no-device recording; device payloads built from the 0.1.0 formatter code (`ViewHierarchyFormatters.kt`, `RunTool.kt`, `ListDevicesTool.kt`). |
| `tapcue-0.1.0/hierarchy-android-settings.synthetic.txt` | **Synthetic**, kept to test text around the JSON (an insight line before the `TreeNode` object). |
| `tapcue-0.1.0/record_mcp_fixture.py` | Recorder. Starts `tapcue mcp` with a minimal environment (no `MAESTRO_*`). |

Recordings must not contain secrets: the recorder passes no `env`, and the smoke runner's transcript goes through the
redactor before it is written.
