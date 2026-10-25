using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MapCue.Adapters.TapCue.FakeServer;

/// <summary>
/// Fake <c>tapcue</c> executable for integration tests. Usage:
/// <c>FakeServer [--fixture f.jsonl] [--scenario s]... [--hierarchy h.json] [--state-dir d] -- &lt;tapcue args&gt;</c>.
/// <c>mcp</c> replays a recorded JSONL fixture (requests are matched by method, tool name and arguments without
/// <c>env</c>). Scenarios inject faults: <c>hang-on:TOOL</c>, <c>exit-on:TOOL</c>, <c>hang-handshake</c>,
/// <c>missing-tool:TOOL</c>, <c>noise</c>, <c>ping-first</c>, <c>echo-env</c>, <c>version:X</c>, <c>hang-cli</c>,
/// <c>driver-lost-once:TOOL</c>, <c>test-fail</c>.
/// In the state directory it writes <c>env.txt</c> (its environment), <c>calls.txt</c> (one tool name per call)
/// and <c>child.pid</c> (a grandchild started before hanging, to verify process-tree kills).
/// </summary>
public static class Program
{
    private static readonly List<string> _scenarios = [];
    private static string? _fixture;
    private static string? _hierarchy;
    private static string? _stateDir;

    public static async Task<int> Main(string[] args)
    {
        var split = Array.IndexOf(args, "--");
        var own = split < 0 ? [] : args[..split];
        var tapcue = split < 0 ? args : args[(split + 1)..];
        for (var i = 0; i + 1 < own.Length; i += 2)
        {
            switch (own[i])
            {
                case "--fixture": _fixture = own[i + 1]; break;
                case "--scenario": _scenarios.Add(own[i + 1]); break;
                case "--hierarchy": _hierarchy = own[i + 1]; break;
                case "--state-dir": _stateDir = own[i + 1]; break;
            }
        }

        if (_stateDir is not null)
        {
            Directory.CreateDirectory(_stateDir);
            var env = Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal)
                .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}");
            await File.WriteAllLinesAsync(Path.Combine(_stateDir, "env.txt"), env);
        }

        var rest = tapcue.ToList();
        if (rest.Count >= 2 && rest[0] == "--device")
        {
            rest.RemoveRange(0, 2);
        }

        return rest.FirstOrDefault() switch
        {
            "--version" => Version(),
            "check-syntax" => CheckSyntax(rest[1]),
            "hierarchy" => await HierarchyAsync(),
            "test" => Test(rest),
            "mcp" => await McpAsync(),
            _ => Usage(),
        };
    }

    private static string? Scenario(string prefix) =>
        _scenarios.FirstOrDefault(s => s == prefix || s.StartsWith(prefix + ":", StringComparison.Ordinal)) is { } s
            ? (s.Length > prefix.Length ? s[(prefix.Length + 1)..] : string.Empty)
            : null;

    private static int Version()
    {
        Console.Out.Write($"tapcue {Scenario("version") ?? "0.1.0"}\nbased on Maestro (Apache-2.0), not affiliated with mobile.dev\n");
        return 0;
    }

    private static int CheckSyntax(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"File does not exist: {path}");
            return 1;
        }

        if (File.ReadAllText(path).Contains("tapOnn", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Invalid Command: tapOnn at /syntax-checker:3:9");
            return 1;
        }

        Console.Out.WriteLine("OK");
        return 0;
    }

    private static async Task<int> HierarchyAsync()
    {
        if (Scenario("hang-cli") is not null)
        {
            await HangAsync();
        }

        Console.Error.WriteLine("Info: fake hierarchy");
        Console.Out.Write(await File.ReadAllTextAsync(_hierarchy ?? throw new InvalidOperationException("--hierarchy required")));
        return 0;
    }

    private static int Test(List<string> args)
    {
        var dir = args[args.IndexOf("--test-output-dir") + 1];
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"fake\":true}");
        File.WriteAllText(Path.Combine(dir, "commands.json"), "[]");
        if (Scenario("test-fail") is not null)
        {
            Console.Error.WriteLine("Element not found: Text matching regex: Nope");
            return 1;
        }

        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("fake tapcue: unsupported arguments");
        return 2;
    }

    private static async Task HangAsync()
    {
        // A grandchild proves that timeouts kill the whole tree, not just the direct child.
        using var child = Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false });
        if (_stateDir is not null && child is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(_stateDir, "child.pid"), child.Id.ToString(CultureInfo.InvariantCulture));
        }

        await Task.Delay(Timeout.Infinite);
    }

    private static async Task<int> McpAsync()
    {
        var replies = LoadFixture();
        var stdout = Console.Out;
        if (Scenario("noise") is not null)
        {
            await stdout.WriteLineAsync("Picked up JAVA_TOOL_OPTIONS: not json");
            await stdout.FlushAsync();
        }

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            var request = JsonNode.Parse(line) as JsonObject;
            var method = (string?)request?["method"];
            var id = request?["id"]?.DeepClone();
            if (request is null || method is null || id is null)
            {
                continue; // notification or response to our ping
            }

            if (method == "initialize")
            {
                if (Scenario("hang-handshake") is not null)
                {
                    await HangAsync();
                }

                if (Scenario("ping-first") is not null)
                {
                    await stdout.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"method\":\"ping\"}");
                    await stdout.FlushAsync();
                }
            }

            var tool = method == "tools/call" ? (string?)request["params"]?["name"] : null;
            if (tool is not null)
            {
                if (_stateDir is not null)
                {
                    await File.AppendAllTextAsync(Path.Combine(_stateDir, "calls.txt"), tool + "\n");
                }

                if (Scenario("hang-on") == tool)
                {
                    await HangAsync();
                }

                if (Scenario("exit-on") == tool)
                {
                    return 3;
                }

                // The first call to TOOL across all fake processes reports a dead device driver (seen on the emulator, R-03).
                if (Scenario("driver-lost-once") == tool && _stateDir is not null && !File.Exists(Path.Combine(_stateDir, "driver-lost")))
                {
                    await File.WriteAllTextAsync(Path.Combine(_stateDir, "driver-lost"), "1");
                    var lost = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Text("Failed to inspect screen: Device server died during 'deviceInfo' on emulator-5554 (10708ms since last byte, connection age 31407ms): StatusRuntimeException: UNAVAILABLE", true) };
                    await stdout.WriteLineAsync(lost.ToJsonString());
                    await stdout.FlushAsync();
                    continue;
                }
            }

            var result = Reply(replies, method, request, tool);
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            await stdout.WriteLineAsync(response.ToJsonString());
            await stdout.FlushAsync();
        }

        return 0;
    }

    private static JsonNode? Reply(Dictionary<string, Queue<JsonNode?>> replies, string method, JsonObject request, string? tool)
    {
        if (tool == "run" && Scenario("echo-env") is not null)
        {
            // Emulates RunTool.runInline: the flow env plus every MAESTRO_* variable of the server is echoed (G-06).
            var env = (request["params"]?["arguments"]?["env"] as JsonObject)?.DeepClone() as JsonObject ?? [];
            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("MAESTRO_", StringComparison.Ordinal)))
            {
                env[name] = Environment.GetEnvironmentVariable(name);
            }

            var payload = new JsonObject { ["success"] = true, ["device_id"] = (string?)request["params"]?["arguments"]?["device_id"], ["commands_executed"] = 2, ["message"] = "Flow executed successfully" };
            if (env.Count > 0)
            {
                payload["env_vars"] = env;
            }

            return Text(payload.ToJsonString(), false);
        }

        var key = Key(method, request);
        if (replies.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            var reply = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            var copy = reply?.DeepClone();
            if (method == "tools/list" && Scenario("missing-tool") is { } missing && copy?["tools"] is JsonArray tools)
            {
                foreach (var t in tools.Where(t => (string?)t?["name"] == missing).ToList())
                {
                    tools.Remove(t);
                }
            }

            return copy;
        }

        return Text($"FAKE: no fixture reply for {key}", true);
    }

    private static JsonObject Text(string text, bool isError)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (isError)
        {
            result["isError"] = true;
        }

        return result;
    }

    private static Dictionary<string, Queue<JsonNode?>> LoadFixture()
    {
        var replies = new Dictionary<string, Queue<JsonNode?>>(StringComparer.Ordinal);
        if (_fixture is null)
        {
            return replies;
        }

        string? pending = null;
        foreach (var line in File.ReadLines(_fixture).Where(l => l.Length > 0))
        {
            var entry = JsonNode.Parse(line)!;
            var msg = entry["msg"] as JsonObject;
            if ((string?)entry["dir"] == "c2s" && msg?["id"] is not null)
            {
                pending = Key((string)msg["method"]!, msg);
            }
            else if ((string?)entry["dir"] == "s2c" && pending is not null)
            {
                if (!replies.TryGetValue(pending, out var queue))
                {
                    replies[pending] = queue = new Queue<JsonNode?>();
                }

                queue.Enqueue(msg?["result"]?.DeepClone());
                pending = null;
            }
        }

        return replies;
    }

    // Requests match on method and, for tool calls, on tool name and arguments without `env` and `yaml`
    // (generated flows differ in names and timeouts from recorded ones).
    private static string Key(string method, JsonObject request)
    {
        if (method != "tools/call")
        {
            return method;
        }

        var args = (request["params"]?["arguments"] as JsonObject)?.DeepClone() as JsonObject ?? [];
        args.Remove("env");
        var hasYaml = args.Remove("yaml");
        var canonical = string.Join(",", args.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value?.ToJsonString()}"));
        return $"tools/call:{(string?)request["params"]?["name"]}:{canonical}{(hasYaml ? ":yaml" : string.Empty)}";
    }
}
