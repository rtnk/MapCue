using MapCue.Adapters.TapCue.Processes;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>Clean child environment (architecture §10.3, G-06/R-13).</summary>
public class ProcessEnvironmentTests
{
    private static readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/op",
        ["JAVA_HOME"] = "/opt/jdk",
        ["MAESTRO_CLOUD_API_KEY"] = "mk-secret",
        ["maestro_lower"] = "x",
        ["JAVA_TOOL_OPTIONS"] = "-javaagent:/tmp/agent.jar",
        ["AWS_SECRET_ACCESS_KEY"] = "aws-secret",
        ["MAPCUE_DEMO_PASSWORD"] = "pw",
        ["CUSTOM_PROXY"] = "http://proxy",
    };

    [Fact]
    public void Only_allowlisted_variables_are_passed()
    {
        var env = ProcessEnvironment.Build([], _parent.GetValueOrDefault);
        Assert.Equal(["HOME", "JAVA_HOME", "PATH"], env.Keys);
    }

    [Fact]
    public void Maestro_variables_are_never_passed_even_when_listed()
    {
        var env = ProcessEnvironment.Build(["MAESTRO_CLOUD_API_KEY", "maestro_lower", "CUSTOM_PROXY"], _parent.GetValueOrDefault);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("MAESTRO_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("http://proxy", env["CUSTOM_PROXY"]);
    }

    [Fact]
    public void Profile_secrets_and_jvm_options_are_not_inherited()
    {
        var env = ProcessEnvironment.Build([], _parent.GetValueOrDefault);
        Assert.False(env.ContainsKey("MAPCUE_DEMO_PASSWORD"));
        Assert.False(env.ContainsKey("AWS_SECRET_ACCESS_KEY"));
        Assert.False(env.ContainsKey("JAVA_TOOL_OPTIONS"));
        Assert.DoesNotContain(ProcessEnvironment.DefaultAllowlist, n => n.Contains("JAVA_OPTIONS", StringComparison.Ordinal) || n.StartsWith("MAESTRO", StringComparison.Ordinal));
    }

    [Fact]
    public void Executable_is_resolved_from_path_or_explicit_file_only()
    {
        var dir = Directory.CreateTempSubdirectory("mapcue-resolve-");
        try
        {
            var file = Path.Combine(dir.FullName, "tapcue");
            File.WriteAllText(file, "#!/bin/sh\n");
            Assert.Equal(file, ExecutableResolver.Resolve("tapcue", "/nonexistent" + Path.PathSeparator + dir.FullName));
            Assert.Equal(file, ExecutableResolver.Resolve(file, null));
            Assert.Null(ExecutableResolver.Resolve("tapcue", "/nonexistent"));
            Assert.Null(ExecutableResolver.Resolve("tapcue", null));
            Assert.Null(ExecutableResolver.Resolve(Path.Combine(dir.FullName, "missing"), null));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
