# Golden agent answers (STEP-04)

Raw answers as a reasoner could return them, validated by `AgentResponseValidator` in
`tests/MapCue.Adapters.Reasoning.Tests/GoldenResponseTests.cs` against the "Profile" screen of `AgentHarness`.
Placeholders are replaced before validation: `{{requestId}}`, `{{snapshotHash}}` (from the request) and
`{{ref:<resourceId or text>}}` (the element ref of that element in the normalized snapshot).
Files are kept byte-for-byte as a model would send them; `*.txt` are not JSON on purpose.
