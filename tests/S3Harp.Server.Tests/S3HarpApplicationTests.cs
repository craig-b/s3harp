using Microsoft.Extensions.DependencyInjection;
using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3HarpApplicationTests : IDisposable
{
    private readonly TempDirectory directory = new("settings");

    [Fact]
    public async Task ReadsTheSettingsFromTheJsonFileTheConfigSettingNames()
    {
        var file = Write(
            "s3harp.json",
            $$"""
            {
              // A comment is fine.
              "access_key_id": "S3HARPFILEKEY",
              "secret_access_key": "file-secret",
              "data_dir": "{{DataDirectory}}",
              "bind": "0.0.0.0",
              "port": 9010,
              "domain": "s3.test",
            }
            """
        );

        await using var app = S3HarpApplication.Build(Settings(("config", file)));

        var options = app.Services.GetRequiredService<S3HarpOptions>();
        Assert.Equal("S3HARPFILEKEY", options.AccessKeyId);
        Assert.Equal("file-secret", options.SecretAccessKey);
        Assert.Equal(DataDirectory, options.DataDirectory);
        Assert.Equal("0.0.0.0", options.Bind);
        Assert.Equal(9010, options.Port);
        Assert.Equal("s3.test", options.Domain);
    }

    [Fact]
    public async Task ReadsTheSettingsFromTheTomlFileTheConfigSettingNames()
    {
        var file = Write(
            "s3harp.toml",
            $"""
            # A comment is fine.
            access_key_id = "S3HARPFILEKEY"
            secret_access_key = "file-secret"
            data_dir = "{DataDirectory}"
            bind = "0.0.0.0"
            port = 9010
            domain = "s3.test"
            """
        );

        await using var app = S3HarpApplication.Build(Settings(("config", file)));

        var options = app.Services.GetRequiredService<S3HarpOptions>();
        Assert.Equal("S3HARPFILEKEY", options.AccessKeyId);
        Assert.Equal("file-secret", options.SecretAccessKey);
        Assert.Equal(DataDirectory, options.DataDirectory);
        Assert.Equal("0.0.0.0", options.Bind);
        Assert.Equal(9010, options.Port);
        Assert.Equal("s3.test", options.Domain);
    }

    [Fact]
    public async Task TheEnvironmentOverridesTheFile()
    {
        var file = Write("s3harp.json", Complete(port: 9010));

        Environment.SetEnvironmentVariable("S3HARP_PORT", "9020");
        try
        {
            await using var app = S3HarpApplication.Build(Settings(("config", file)));

            Assert.Equal(9020, app.Services.GetRequiredService<S3HarpOptions>().Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("S3HARP_PORT", null);
        }
    }

    [Fact]
    public async Task AFlagOverridesTheFile()
    {
        var file = Write("s3harp.json", Complete(port: 9010));

        await using var app = S3HarpApplication.Build(Settings(("config", file), ("port", "9030")));

        Assert.Equal(9030, app.Services.GetRequiredService<S3HarpOptions>().Port);
    }

    [Fact]
    public void RefusesToStartWhenTheFileDoesNotExist()
    {
        var file = Path.Combine(directory.Path, "missing.json");

        var exception = Assert.Throws<StartupException>(() =>
            S3HarpApplication.Build(Settings(("config", file)))
        );

        Assert.Equal(
            $"S3Harp cannot start: config names {file}, which does not exist.",
            exception.Message
        );
    }

    [Fact]
    public void RefusesAFileInAFormatItDoesNotRead()
    {
        var file = Write("s3harp.yaml", "port: 9010");

        var exception = Assert.Throws<StartupException>(() =>
            S3HarpApplication.Build(Settings(("config", file)))
        );

        Assert.Equal(
            $"S3Harp cannot start: config must name a .json or .toml file, not {file}.",
            exception.Message
        );
    }

    [Fact]
    public void RefusesAFileThatIsNotValidJson()
    {
        var file = Write("s3harp.json", "{ \"port\": 9010");

        var exception = Assert.Throws<StartupException>(() =>
            S3HarpApplication.Build(Settings(("config", file)))
        );

        Assert.StartsWith(
            $"S3Harp cannot start: {file} is not valid JSON: ",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void RefusesAFileThatIsNotValidToml()
    {
        var file = Write("s3harp.toml", "port = 9010\nname = \n");

        var exception = Assert.Throws<StartupException>(() =>
            S3HarpApplication.Build(Settings(("config", file)))
        );

        Assert.StartsWith(
            $"S3Harp cannot start: {file} is not valid TOML: (2,",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    public void Dispose() => directory.Dispose();

    private string DataDirectory => Path.Combine(directory.Path, "data");

    private string Complete(int port) =>
        $$"""
            {
              "access_key_id": "S3HARPFILEKEY",
              "secret_access_key": "file-secret",
              "data_dir": "{{DataDirectory}}",
              "port": {{port}}
            }
            """;

    private string Write(string name, string content)
    {
        var path = Path.Combine(directory.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Dictionary<string, string?> Settings(
        params (string Key, string Value)[] settings
    )
    {
        var all = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        return all;
    }
}
