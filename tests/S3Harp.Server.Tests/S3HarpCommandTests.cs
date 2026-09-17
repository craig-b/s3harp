using System.CommandLine;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3HarpCommandTests
{
    [Fact]
    public void EachFlagSetsItsSetting()
    {
        var parsed = S3HarpCommand
            .Create(_ => 0)
            .Parse([
                "--access-key-id",
                "S3HARPEXAMPLEKEY",
                "--secret-access-key",
                "secret",
                "--data-dir",
                "/tmp/data",
                "--bind",
                "0.0.0.0",
                "--port",
                "9010",
                "--domain",
                "s3.test",
            ]);

        Assert.Empty(parsed.Errors);
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["ACCESS_KEY_ID"] = "S3HARPEXAMPLEKEY",
                ["SECRET_ACCESS_KEY"] = "secret",
                ["DATA_DIR"] = "/tmp/data",
                ["BIND"] = "0.0.0.0",
                ["PORT"] = "9010",
                ["DOMAIN"] = "s3.test",
            },
            S3HarpCommand.Settings(parsed)
        );
    }

    [Fact]
    public void OnlyTheFlagsGivenBecomeSettings()
    {
        var parsed = S3HarpCommand.Create(_ => 0).Parse(["--port", "9010"]);

        Assert.Equal(
            new Dictionary<string, string?> { ["PORT"] = "9010" },
            S3HarpCommand.Settings(parsed)
        );
    }

    [Fact]
    public void AnUnknownFlagIsAnError()
    {
        var parsed = S3HarpCommand.Create(_ => 0).Parse(["--colour", "red"]);

        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void HelpNamesEveryFlagWithItsEnvironmentVariable()
    {
        using var output = new StringWriter();

        var exitCode = S3HarpCommand
            .Create(_ => 0)
            .Parse(["--help"])
            .Invoke(new InvocationConfiguration { Output = output });

        Assert.Equal(0, exitCode);
        var help = output.ToString();
        foreach (
            var (flag, variable) in new[]
            {
                ("--access-key-id", "S3HARP_ACCESS_KEY_ID"),
                ("--secret-access-key", "S3HARP_SECRET_ACCESS_KEY"),
                ("--data-dir", "S3HARP_DATA_DIR"),
                ("--bind", "S3HARP_BIND"),
                ("--port", "S3HARP_PORT"),
                ("--domain", "S3HARP_DOMAIN"),
            }
        )
        {
            Assert.Contains(flag, help, StringComparison.Ordinal);
            Assert.Contains(variable, help, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheStartActionReceivesTheSettingsAndItsExitCodeIsReturned()
    {
        IReadOnlyDictionary<string, string?>? received = null;

        var exitCode = S3HarpCommand
            .Create(settings =>
            {
                received = settings;
                return 3;
            })
            .Parse(["--port", "1"])
            .Invoke();

        Assert.Equal(3, exitCode);
        Assert.Equal("1", received?["PORT"]);
    }
}
