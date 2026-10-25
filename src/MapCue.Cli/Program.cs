using MapCue.Cli;

using var host = new CliHost(Console.Error);
return await MapCueCli.RunAsync(args, Console.Out, Console.Error, host);
