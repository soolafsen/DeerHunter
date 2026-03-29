using DeerHunter.Configuration;
using Microsoft.Extensions.Options;

namespace DeerHunter.Tests;

public sealed class HostConfigurationTests
{
    [Fact]
    public void ResolveConfigPath_PrefersCommandLineOverride()
    {
        var path = DeerHunterHost.ResolveConfigPath(["--config", "custom.json"], configuredPath: "ignored.json");

        Assert.Equal("custom.json", path);
    }

    [Fact]
    public void ResolveConfigPath_FallsBackToDefaultFile()
    {
        var path = DeerHunterHost.ResolveConfigPath([], configuredPath: null);

        Assert.Equal("deerhunter.json", path);
    }

    [Fact]
    public void Validator_RejectsDuplicateProcessNames()
    {
        var options = new DeerHunterOptions
        {
            Processes =
            [
                new ManagedProcessOptions { Name = "alpha", Command = "dotnet" },
                new ManagedProcessOptions { Name = "alpha", Command = "dotnet" }
            ]
        };

        var result = new DeerHunterOptionsValidator().Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("Duplicate process name 'alpha'.", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatConfigurationError_FormatsValidationFailures()
    {
        var exception = new OptionsValidationException(
            nameof(DeerHunterOptions),
            typeof(DeerHunterOptions),
            ["Every process must define a name."]);

        var handled = DeerHunterHost.TryFormatConfigurationError(exception, out var message);

        Assert.True(handled);
        Assert.Equal("Configuration validation failed: Every process must define a name.", message);
    }
}
