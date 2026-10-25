using MapCue.Application.Configuration;

namespace MapCue.Application.Tests;

/// <summary>The optional <c>tapcue</c> section (STEP-02, docs/tapcue-compatibility.md §8).</summary>
public class TapCueConfigTests
{
    private static MapCueConfigDocument With(TapcueDocument? tapcue) => new()
    {
        SchemaVersion = "mapcue.config/1",
        Allowlist = new AllowlistDocument { AppIds = ["com.example.demo"], Environments = ["test"] },
        Tapcue = tapcue,
    };

    [Fact]
    public void Defaults_pin_the_audited_version_and_use_path_lookup()
    {
        var s = MapCueConfigValidator.Validate(With(null)).Settings!.TapCue;
        Assert.Equal(TapCueSettings.Default, s);
        Assert.Equal("tapcue", s.Executable);
        Assert.Equal(["0.1.0"], s.SupportedVersions);
        Assert.False(s.AllowUntestedVersion);
        Assert.Empty(s.PassEnvironment);
        Assert.Equal(FullObservationMode.Shared, s.FullObservation);
        Assert.True(s.CheckGeneratedSyntax);
    }

    [Fact]
    public void All_keys_are_bound()
    {
        var s = MapCueConfigValidator.Validate(With(new TapcueDocument
        {
            Executable = "/opt/tapcue/bin/tapcue",
            SupportedVersions = ["0.1.0", "0.1.1"],
            AllowUntestedVersion = true,
            PassEnvironment = ["HTTPS_PROXY", "HTTPS_PROXY"],
            StartupTimeout = "3m",
            FullObservation = "exclusive",
            CheckGeneratedSyntax = false,
        })).Settings!.TapCue;
        Assert.Equal(new TapCueSettings("/opt/tapcue/bin/tapcue", s.SupportedVersions, true, s.PassEnvironment, TimeSpan.FromMinutes(3), FullObservationMode.Exclusive, false), s);
        Assert.Equal(["0.1.0", "0.1.1"], s.SupportedVersions);
        Assert.Equal(["HTTPS_PROXY"], s.PassEnvironment);
    }

    [Theory]
    [InlineData("MAESTRO_CLOUD_API_KEY", ConfigErrorCodes.ForbiddenPassEnvironment)]
    [InlineData("maestro_x", ConfigErrorCodes.ForbiddenPassEnvironment)]
    [InlineData("NOT-A-NAME", ConfigErrorCodes.InvalidTapCueSetting)]
    public void Maestro_variables_cannot_be_passed(string name, string code)
    {
        var result = MapCueConfigValidator.Validate(With(new TapcueDocument { PassEnvironment = [name] }));
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(name, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("executable", "")]
    [InlineData("executable", "tap\ncue")]
    [InlineData("version", "latest")]
    [InlineData("versions", "")]
    [InlineData("startupTimeout", "1s")]
    [InlineData("startupTimeout", "2h")]
    [InlineData("startupTimeout", "soon")]
    [InlineData("fullObservation", "parallel")]
    public void Invalid_values_are_rejected(string key, string value)
    {
        var doc = key switch
        {
            "executable" => new TapcueDocument { Executable = value },
            "version" => new TapcueDocument { SupportedVersions = [value] },
            "versions" => new TapcueDocument { SupportedVersions = [] },
            "startupTimeout" => new TapcueDocument { StartupTimeout = value },
            _ => new TapcueDocument { FullObservation = value },
        };
        var result = MapCueConfigValidator.Validate(With(doc));
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }
}
