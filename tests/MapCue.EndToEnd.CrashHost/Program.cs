using System.Diagnostics;
using System.Globalization;
using MapCue.Domain.Graph;
using MapCue.EndToEnd.Tests;
using MapCue.Infrastructure.Sqlite;

// Usage: MapCue.EndToEnd.CrashHost <workspace> <phase> <occurrence> <seed>
// Explores the demo model (FakeDevice) in <workspace> holding the workspace lock, like `mapcue explore`, and kills its own process
// (SIGKILL: no finally blocks, no dispose, no rollback) right after the <occurrence>-th attempt journaled <phase>. Before dying it
// writes crashed.txt (task id, attempt id, phase). Exit code 3: the phase was never reached.
if (args.Length != 4)
{
    await Console.Error.WriteLineAsync("usage: MapCue.EndToEnd.CrashHost <workspace> <phase> <occurrence> <seed>");
    return 2;
}

var root = args[0];
var phase = Enum.Parse<AttemptPhase>(args[1]);
var occurrence = int.Parse(args[2], CultureInfo.InvariantCulture);
var seed = args[3];

using var workspaceLock = WorkspaceLock.Acquire(root);
await using var harness = ExplorationHarness.Open(root);
var seen = 0;
var options = harness.Options(seed) with
{
    WorkspaceLockHeld = true,
    OnAttemptPhase = e =>
    {
        if (e.Phase != phase || ++seen != occurrence)
        {
            return;
        }

        File.WriteAllText(Path.Combine(root, "crashed.txt"), $"{e.TaskId}\n{e.AttemptId}\n{e.Phase}\n");
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    },
};

var result = await harness.Engine(seed: seed).RunAsync(options, CancellationToken.None);
Console.WriteLine($"finished without crash: {result.Reason}");
return 3;
