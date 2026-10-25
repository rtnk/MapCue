using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MapCue.Adapters.TapCue.Flows;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Application.Time;
using MapCue.Domain;
using MapCue.Infrastructure.Artifacts.Redaction;

namespace MapCue.Adapters.TapCue.Smoke;

/// <summary>
/// STEP-02 smoke of the TapCue adapter (docs/ROADMAP.md): list_devices → launchApp → inspect_screen → safe action →
/// inspect_screen → hierarchy → test --test-output-dir, with timings (ADR-0002 PoC) and the MCP/CLI collision check (R-03).
/// <code>
/// mapcue-smoke --out DIR [--tapcue PATH] [--templates-only] [--device ID --app-id APP --safe-action back|scroll]
/// </code>
/// Writes report.json, report.md, mcp-transcript.jsonl (redacted, fixture format) and raw observations into DIR.
/// Exit code 0 when the required steps pass (screen read and safe action on the device, or all templates valid).
/// </summary>
public static class Program
{
    private static readonly List<Step> _steps = [];
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var options = Parse(args);
        var outDir = Path.GetFullPath(options.GetValueOrDefault("--out") ?? "smoke-out");
        Directory.CreateDirectory(outDir);
        var appId = options.GetValueOrDefault("--app-id") ?? "com.android.settings";
        var deviceArg = options.GetValueOrDefault("--device");
        var safeAction = options.GetValueOrDefault("--safe-action") ?? "back";
        var settings = TapCueSettings.Default with { Executable = options.GetValueOrDefault("--tapcue") ?? TapCueSettings.DefaultExecutable };
        var budgets = Budgets.Default with { ActionTimeout = TimeSpan.FromSeconds(60), ObserveTimeout = TimeSpan.FromSeconds(60) };
        var redactor = new PatternRedactor();
        var adapterOptions = new TapCueAdapterOptions(settings, budgets, [appId], deviceArg is null ? [] : [deviceArg], Path.Combine(outDir, "work"), "smoke");

        using var transcript = new RedactingTranscriptFile(Path.Combine(outDir, "mcp-transcript.jsonl"), redactor);
        var adapter = new TapCueDeviceAutomation(adapterOptions, redactor, new SystemClock(), transcript: transcript);
        await using (adapter.ConfigureAwait(false))
        {
            await RunStep("version", true, async () =>
            {
                var (version, status) = await adapter.PreflightAsync(CancellationToken.None);
                return $"{version.Name} {version.Version} ({status})";
            });

            await TemplatesAsync(adapter, appId, outDir);
            if (options.ContainsKey("--templates-only"))
            {
                return Finish(outDir, "templates-only");
            }

            DeviceId device = default;
            await RunStep("list_devices", true, async () =>
            {
                var devices = await adapter.ListDevicesAsync(CancellationToken.None);
                var connected = devices.Where(d => d.IsConnected).ToList();
                var chosen = deviceArg is not null ? connected.FirstOrDefault(d => d.Id.Value == deviceArg) : connected.FirstOrDefault();
                device = chosen?.Id ?? throw new InvalidOperationException($"no connected device{(deviceArg is null ? string.Empty : " " + deviceArg)} among {devices.Count}");
                return $"{device} ({chosen.Platform}); MCP session starts: {adapter.SessionStarts}";
            });
            if (device == default)
            {
                return Finish(outDir, "no device");
            }

            await Act("launchApp", adapter, device, new ValidatedDeviceAction("smoke-launch", appId, ActionType.LaunchApp, RiskClass.Navigational, null, null, null));
            await Observe("inspect_screen.1", adapter, device, appId, ObservationFidelity.Fast, outDir, required: true);
            await Observe("inspect_screen.2 (warm)", adapter, device, appId, ObservationFidelity.Fast, outDir, required: false);
            var action = safeAction == "scroll"
                ? new ValidatedDeviceAction("smoke-scroll", appId, ActionType.Scroll, RiskClass.Safe, null, null, null, ScrollDirection.Down)
                : new ValidatedDeviceAction("smoke-back", appId, ActionType.Back, RiskClass.Navigational, null, null, null);
            await Act(safeAction, adapter, device, action);
            await Observe("inspect_screen.after-action", adapter, device, appId, ObservationFidelity.Fast, outDir, required: true);

            // R-03: CLI hierarchy while the MCP session is alive, then MCP again.
            var startsBefore = adapter.SessionStarts;
            await Observe("hierarchy (shared, MCP alive)", adapter, device, appId, ObservationFidelity.Full, outDir, required: false);
            await Observe("inspect_screen.after-hierarchy", adapter, device, appId, ObservationFidelity.Fast, outDir, required: false);
            Note("R-03.hierarchy", adapter.SessionStarts == startsBefore
                ? "MCP session survived CLI hierarchy (no restart)"
                : $"MCP session restarted {adapter.SessionStarts - startsBefore} time(s) after CLI hierarchy");

            var replay = Path.Combine(outDir, "replay.yaml");
            await File.WriteAllTextAsync(replay, $"appId: {MaestroText.YamlQuote(appId)}\nname: \"mapcue-smoke-replay\"\n---\n- launchApp\n- waitForAnimationToEnd:\n    timeout: 5000\n- takeScreenshot: replay-launched\n");
            startsBefore = adapter.SessionStarts;
            await RunStep("test --test-output-dir", false, async () =>
            {
                var result = await adapter.RunFlowAsync(device, new FlowFile(replay), new FlowRunOptions(TimeSpan.FromMinutes(5), Path.Combine(outDir, "test-output")), CancellationToken.None);
                var files = result.TestOutputDirectory is { } dir && Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Order(StringComparer.Ordinal).ToList()
                    : [];
                var expected = new[] { "manifest.json", "commands.json" }.Where(f => !files.Contains(f)).ToList();
                if (result.Outcome != ActionOutcome.Succeeded || expected.Count > 0)
                {
                    throw new InvalidOperationException($"{result.Outcome} {result.ErrorKind}: {result.Detail}; missing: {string.Join(", ", expected)}; files: {string.Join(", ", files.Take(20))}");
                }

                return $"files: {string.Join(", ", files.Take(20))}";
            });
            await Observe("inspect_screen.after-test", adapter, device, appId, ObservationFidelity.Fast, outDir, required: false);
            Note("R-03.test", adapter.SessionStarts == startsBefore
                ? "MCP session survived CLI test (no restart)"
                : $"MCP session restarted {adapter.SessionStarts - startsBefore} time(s) after CLI test");

            // Exclusive mode for comparison: the MCP session is closed for the CLI call and restarted afterwards.
            var exclusive = new TapCueDeviceAutomation(adapterOptions with { TapCue = settings with { FullObservation = FullObservationMode.Exclusive }, WorkDirectory = Path.Combine(outDir, "work-exclusive") }, redactor, new SystemClock());
            await using (exclusive.ConfigureAwait(false))
            {
                await Observe("exclusive: inspect_screen", exclusive, device, appId, ObservationFidelity.Fast, outDir, required: false);
                await Observe("exclusive: hierarchy", exclusive, device, appId, ObservationFidelity.Full, outDir, required: false);
                await Observe("exclusive: inspect_screen (restarted session)", exclusive, device, appId, ObservationFidelity.Fast, outDir, required: false);
                Note("exclusive.sessionStarts", exclusive.SessionStarts.ToString(CultureInfo.InvariantCulture));
            }
        }

        return Finish(outDir, "device");
    }

    private static async Task TemplatesAsync(TapCueDeviceAutomation adapter, string appId, string outDir)
    {
        var generator = new FlowYamlGenerator([appId], TimeSpan.FromSeconds(5));
        ValidatedDeviceAction A(string id, ActionType type, string? rid = null, string? text = null, string? input = null, ScrollDirection? dir = null) =>
            new(id, appId, type, RiskClass.Safe, rid, text, input, dir);
        var samples = new[]
        {
            A("t-tap-id", ActionType.Tap, rid: "android:id/title"),
            A("t-tap-text", ActionType.Tap, text: "Network & internet (1) ${x} a.b*"),
            A("t-tap-both", ActionType.Tap, rid: "row", text: "Apps"),
            A("t-longpress", ActionType.LongPress, text: "Apps"),
            A("t-back", ActionType.Back),
            A("t-launch", ActionType.LaunchApp),
            A("t-hide", ActionType.HideKeyboard),
            A("t-wait", ActionType.WaitForAnimationToEnd),
            A("t-scroll", ActionType.Scroll),
            A("t-scroll-up", ActionType.Scroll, dir: ScrollDirection.Up),
            A("t-swipe", ActionType.Swipe, dir: ScrollDirection.Left),
            A("t-input", ActionType.InputText, input: "profile.inputs.username"),
        };
        var dir = Path.Combine(outDir, "templates");
        Directory.CreateDirectory(dir);
        foreach (var sample in samples)
        {
            var flow = generator.Generate(sample, _ => "value");
            var path = Path.Combine(dir, sample.ActionId + ".yaml");
            await File.WriteAllTextAsync(path, flow.Yaml, new UTF8Encoding(false));
            await RunStep($"check-syntax {flow.ShapeKey}", true, async () =>
            {
                var result = await adapter.CheckSyntaxAsync(new FlowFile(path), CancellationToken.None);
                return result.IsValid ? "OK" : throw new InvalidOperationException(string.Join("; ", result.Errors));
            });
        }
    }

    private static async Task Act(string name, TapCueDeviceAutomation adapter, DeviceId device, ValidatedDeviceAction action) =>
        await RunStep(name, true, async () =>
        {
            var result = await adapter.ExecuteAsync(device, action, CancellationToken.None);
            return result.Outcome == ActionOutcome.Succeeded
                ? $"{result.Outcome} in {result.Duration.TotalSeconds:0.00} s: {result.Detail}"
                : throw new InvalidOperationException($"{result.Outcome} {result.ErrorKind} (dispatched: {result.Dispatched}): {result.Detail}");
        });

    private static async Task Observe(string name, TapCueDeviceAutomation adapter, DeviceId device, string appId, ObservationFidelity fidelity, string outDir, bool required) =>
        await RunStep(name, required, async () =>
        {
            var observation = await adapter.ObserveAsync(device, new ObservationRequest(appId, fidelity), CancellationToken.None);
            var file = Path.Combine(outDir, Slug(name) + ".json");
            await File.WriteAllTextAsync(file, observation.RawPayload ?? string.Empty);
            var sample = string.Join(" | ", observation.Elements.Select(e => e.Text ?? e.AccessibilityText ?? e.ResourceId).Where(t => t is not null).Take(6));
            return observation.Elements.Count > 0
                ? $"{observation.Elements.Count} elements, {observation.SnapshotHash[..19]}; e.g. {sample}"
                : throw new InvalidOperationException("observation has no elements");
        });

    private static async Task RunStep(string name, bool required, Func<Task<string>> body)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var detail = await body();
            _steps.Add(new Step(name, "PASS", stopwatch.Elapsed.TotalSeconds, detail, required));
        }
        catch (Exception ex)
        {
            _steps.Add(new Step(name, "FAIL", stopwatch.Elapsed.TotalSeconds, $"{ex.GetType().Name}: {ex.Message}", required));
        }

        var last = _steps[^1];
        Console.WriteLine($"{last.Status} {last.Seconds,7:0.00}s {last.Name}: {last.Detail}");
    }

    private static void Note(string name, string detail)
    {
        _steps.Add(new Step(name, "INFO", 0, detail, false));
        Console.WriteLine($"INFO          {name}: {detail}");
    }

    private static int Finish(string outDir, string mode)
    {
        var failedRequired = _steps.Where(s => s.Required && s.Status == "FAIL").ToList();
        var report = new
        {
            schemaVersion = "mapcue.tapcue-smoke/1",
            mode,
            result = failedRequired.Count == 0 ? "PASS" : "FAIL",
            host = $"{Environment.OSVersion} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
            steps = _steps,
        };
        File.WriteAllText(Path.Combine(outDir, "report.json"), JsonSerializer.Serialize(report, _json));
        var md = new StringBuilder($"# TapCue adapter smoke ({mode}): {report.result}\n\nHost: {report.host}\n\n| Step | Status | Seconds | Required | Detail |\n|---|---|---|---|---|\n");
        foreach (var s in _steps)
        {
            md.Append(CultureInfo.InvariantCulture, $"| {s.Name} | {s.Status} | {s.Seconds:0.00} | {(s.Required ? "yes" : "no")} | {s.Detail.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ")} |\n");
        }

        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
        Console.WriteLine($"RESULT {report.result} ({mode}); report: {Path.Combine(outDir, "report.md")}");
        return failedRequired.Count == 0 ? 0 : 1;
    }

    private static string Slug(string name) => new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--templates-only")
            {
                result[args[i]] = "true";
            }
            else if (i + 1 < args.Length)
            {
                result[args[i]] = args[++i];
            }
        }

        return result;
    }

    private sealed record Step(string Name, string Status, double Seconds, string Detail, bool Required);
}
