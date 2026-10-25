# TapCue fixtures (contract tests)

One directory per supported TapCue version (`tapcue-<version>/`). A new TapCue version needs new fixtures and a passing
smoke before it is added to `tapcue.supportedVersions` (docs/tapcue-compatibility.md §8, R-12).

Transcripts are JSONL: one `{"dir": "c2s" | "s2c", "msg": <JSON-RPC message>}` per line. They are parsed by the
contract tests and replayed by the fake server (`tests/MapCue.Adapters.TapCue.FakeServer`) in integration tests.

| File | Provenance |
|---|---|
| `tapcue-0.1.0/mcp-no-device.jsonl` | **Recorded** from the real `tapcue mcp` 0.1.0 (release archive, SHA-256 `e8dd01b0…6ec1`) on Linux x86_64 without a device, with `record_mcp_fixture.py`. CI re-records it in `tapcue-smoke.yml` (`tapcue-cli` job) and fails on drift. |
| `tapcue-0.1.0/mcp-android-settings.synthetic.jsonl` | **Synthetic**: handshake and `tools/list` copied from the recording above; device payloads built from the 0.1.0 formatter code (`ViewHierarchyFormatters.kt`, `RunTool.kt`, `ListDevicesTool.kt`). Replace with an emulator recording (`mcp-device-recording.jsonl` artifact of the Android smoke) when available. |
| `tapcue-0.1.0/hierarchy-android-settings.synthetic.txt` | **Synthetic**: `tapcue hierarchy` stdout shape (Jackson pretty-printed `TreeNode`, preceded by an insight line). Replace with `hierarchy-raw.txt` from the Android smoke artifact. |
| `tapcue-0.1.0/record_mcp_fixture.py` | Recorder. Starts `tapcue mcp` with a minimal environment (no `MAESTRO_*`). |

Recordings must not contain secrets: the recorder passes no `env`, and the smoke runner's transcript goes through the
redactor before it is written.
