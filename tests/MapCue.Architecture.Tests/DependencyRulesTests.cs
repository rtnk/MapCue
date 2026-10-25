using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MapCue.Architecture.Tests;

/// <summary>
/// Enforces architecture §3.1: dependencies point inwards (Cli → Adapters/Infrastructure → Application → Domain),
/// Contracts and Domain depend on nothing, adapters/infrastructure never reference each other.
/// Checked on project files (declared) and on compiled assemblies (actual).
/// </summary>
public partial class DependencyRulesTests
{
    private static readonly string[] _core = ["MapCue.Domain", "MapCue.Contracts"];
    private static readonly string[] _inner = ["MapCue.Application", .. _core];

    // Allowed project references per src project. Anything not listed is a violation.
    private static readonly Dictionary<string, string[]> _allowed = new(StringComparer.Ordinal)
    {
        ["MapCue.Domain"] = [],
        ["MapCue.Contracts"] = [],
        ["MapCue.Application"] = _core,
        ["MapCue.Infrastructure.Sqlite"] = _inner,
        ["MapCue.Infrastructure.Artifacts"] = _inner,
        ["MapCue.Adapters.TapCue"] = _inner,
        ["MapCue.Adapters.Reasoning"] = _inner,
        ["MapCue.Reporting"] = _inner,
        ["MapCue.Cli"] =
        [
            .. _inner,
            "MapCue.Infrastructure.Sqlite", "MapCue.Infrastructure.Artifacts",
            "MapCue.Adapters.TapCue", "MapCue.Adapters.Reasoning", "MapCue.Reporting",
        ],
    };

    // Packages permitted per project; Domain and Contracts must stay dependency-free.
    private static readonly Dictionary<string, string[]> _allowedPackages = new(StringComparer.Ordinal)
    {
        ["MapCue.Domain"] = [],
        ["MapCue.Contracts"] = [],
        ["MapCue.Application"] = [],
    };

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MapCue.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("MapCue.sln not found.");
        }
    }

    private static IEnumerable<string> SourceProjects() =>
        Directory.GetFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    private static IEnumerable<string> AllProjects() =>
        SourceProjects().Concat(Directory.GetFiles(Path.Combine(RepoRoot, "tests"), "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal));

    private static string NameOf(string csproj) => Path.GetFileNameWithoutExtension(csproj);

    private static IEnumerable<string> ProjectReferences(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')));

    private static IEnumerable<string> PackageReferences(string csproj) =>
        XDocument.Load(csproj).Descendants("PackageReference").Select(e => e.Attribute("Include")!.Value);

    [Fact]
    public void Every_source_project_has_a_rule()
    {
        Assert.Equal(_allowed.Keys.Order(StringComparer.Ordinal), SourceProjects().Select(NameOf));
    }

    [Fact]
    public void Source_projects_only_reference_allowed_projects()
    {
        var violations = SourceProjects()
            .SelectMany(p => ProjectReferences(p).Where(r => !_allowed[NameOf(p)].Contains(r)).Select(r => $"{NameOf(p)} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Source_projects_never_reference_test_projects()
    {
        var violations = SourceProjects()
            .SelectMany(p => ProjectReferences(p).Where(r => r.EndsWith("Tests", StringComparison.Ordinal) || r == "MapCue.Testing").Select(r => $"{NameOf(p)} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Testing_library_only_uses_inner_layers()
    {
        var testing = Path.Combine(RepoRoot, "tests", "MapCue.Testing", "MapCue.Testing.csproj");
        Assert.All(ProjectReferences(testing), r => Assert.Contains(r, _inner));
    }

    [Fact]
    public void Core_projects_have_no_package_dependencies()
    {
        var violations = SourceProjects()
            .Where(p => _allowedPackages.ContainsKey(NameOf(p)))
            .SelectMany(p => PackageReferences(p).Where(pkg => !_allowedPackages[NameOf(p)].Contains(pkg)).Select(pkg => $"{NameOf(p)} -> {pkg}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void No_project_depends_on_tapcue_internals_or_maui_dev_mcp()
    {
        // ADR-0002: TapCue only through its public CLI/MCP processes, never as a library.
        var violations = AllProjects()
            .SelectMany(p => PackageReferences(p).Concat(ProjectReferences(p)).Where(r => !r.StartsWith("MapCue.", StringComparison.Ordinal) && ForbiddenDependency().IsMatch(r)).Select(r => $"{NameOf(p)} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Projects_do_not_override_central_settings()
    {
        string[] forbidden = ["TargetFramework", "TargetFrameworks", "Nullable", "TreatWarningsAsErrors", "AnalysisLevel", "Version"];
        var violations = AllProjects()
            .SelectMany(p => XDocument.Load(p).Descendants().Where(e => forbidden.Contains(e.Name.LocalName)).Select(e => $"{NameOf(p)}: {e.Name.LocalName}"))
            .Concat(AllProjects().SelectMany(p => XDocument.Load(p).Descendants("PackageReference").Where(e => e.Attribute("Version") is not null).Select(_ => $"{NameOf(p)}: inline package version")))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Every_project_is_in_the_solution()
    {
        var sln = File.ReadAllText(Path.Combine(RepoRoot, "MapCue.sln"));
        Assert.All(AllProjects(), p => Assert.Contains(NameOf(p) + ".csproj", sln, StringComparison.Ordinal));
    }

    public static TheoryData<string> CompiledAssemblies() => new(_allowed.Keys.Where(k => k != "MapCue.Cli"));

    [Theory]
    [MemberData(nameof(CompiledAssemblies))]
    public void Compiled_assemblies_respect_the_rules(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var mapCueReferences = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("MapCue.", StringComparison.Ordinal));
        Assert.All(mapCueReferences, r => Assert.Contains(r, _allowed[assemblyName]));
    }

    [Fact]
    public void Cli_assembly_respects_the_rules()
    {
        var cli = Assembly.Load("mapcue");
        Assert.All(
            cli.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("MapCue.", StringComparison.Ordinal)),
            r => Assert.Contains(r, _allowed["MapCue.Cli"]));
    }

    [GeneratedRegex("(?i)(tapcue|maestro|maui-dev-mcp|mauidevmcp)")]
    private static partial Regex ForbiddenDependency();
}
