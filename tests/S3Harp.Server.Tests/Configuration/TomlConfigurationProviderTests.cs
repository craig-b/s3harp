using Microsoft.Extensions.Configuration;
using S3Harp.Server.Configuration;
using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Server.Tests.Configuration;

public sealed class TomlConfigurationProviderTests : IDisposable
{
    private readonly TempDirectory directory = new("toml");

    [Fact]
    public void ReadsEachKindOfValueAsItsInvariantText()
    {
        var configuration = Read(
            """
            name = "s3harp"
            port = 9010
            ratio = 1.5
            enabled = true
            disabled = false
            started = 2026-09-20T07:30:00Z
            """
        );

        Assert.Equal("s3harp", configuration["name"]);
        Assert.Equal("9010", configuration["port"]);
        Assert.Equal("1.5", configuration["ratio"]);
        Assert.Equal("true", configuration["enabled"]);
        Assert.Equal("false", configuration["disabled"]);
        Assert.Equal("2026-09-20T07:30:00Z", configuration["started"]);
    }

    [Fact]
    public void LooksKeysUpWithoutRegardToCase() =>
        Assert.Equal("9010", Read("Port = 9010")["port"]);

    [Fact]
    public void ReadsTablesAndDottedKeysAsSections()
    {
        var configuration = Read(
            """
            top.level = "a"

            [Logging.LogLevel]
            Default = "Debug"
            "S3Harp.Server" = "Trace"

            [server]
            listen.port = 1
            """
        );

        Assert.Equal("a", configuration["top:level"]);
        Assert.Equal("Debug", configuration["Logging:LogLevel:Default"]);
        Assert.Equal("Trace", configuration["Logging:LogLevel:S3Harp.Server"]);
        Assert.Equal("1", configuration["server:listen:port"]);
    }

    [Fact]
    public void ReadsArraysAsIndexedKeys()
    {
        var configuration = Read(
            """
            ports = [1, 2, 3]
            nested = [["a", "b"], ["c"]]
            """
        );

        Assert.Equal(
            ["1", "2", "3"],
            configuration.GetSection("ports").GetChildren().Select(c => c.Value)
        );
        Assert.Equal("b", configuration["nested:0:1"]);
        Assert.Equal("c", configuration["nested:1:0"]);
    }

    [Fact]
    public void ReadsInlineTablesAsSections()
    {
        var configuration = Read("""point = { x = 1, y = { z = 2 } }""");

        Assert.Equal("1", configuration["point:x"]);
        Assert.Equal("2", configuration["point:y:z"]);
    }

    [Fact]
    public void ReadsArraysOfTablesAsIndexedSections()
    {
        var configuration = Read(
            """
            [[keys]]
            id = "first"

            [[keys]]
            id = "second"

            [keys.scope]
            bucket = "photos"

            [[keys.grants]]
            action = "read"

            [[keys.grants]]
            action = "write"
            """
        );

        Assert.Equal("first", configuration["keys:0:id"]);
        Assert.Equal("second", configuration["keys:1:id"]);
        Assert.Equal("photos", configuration["keys:1:scope:bucket"]);
        Assert.Equal("read", configuration["keys:1:grants:0:action"]);
        Assert.Equal("write", configuration["keys:1:grants:1:action"]);
    }

    [Fact]
    public void ReportsWhereAMalformedFileWentWrong()
    {
        var path = Write("bad.toml", "port = 9010\nname = \n");

        var exception = Assert.Throws<InvalidDataException>(() =>
            Read(new TomlConfigurationSource { Path = path })
        );

        Assert.StartsWith("(2,", Innermost(exception).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsADuplicateKey()
    {
        var path = Write("dup.toml", "port = 1\nport = 2\n");

        var exception = Assert.Throws<InvalidDataException>(() =>
            Read(new TomlConfigurationSource { Path = path })
        );

        Assert.StartsWith("(2,", Innermost(exception).Message, StringComparison.Ordinal);
        Assert.Contains("`port`", Innermost(exception).Message, StringComparison.Ordinal);
    }

    public void Dispose() => directory.Dispose();

    /// <summary>The provider's own exception under the framework's "failed to load" wrapping.</summary>
    private static Exception Innermost(Exception exception) =>
        exception.InnerException is { } inner ? Innermost(inner) : exception;

    private IConfiguration Read(string toml) =>
        Read(new TomlConfigurationSource { Path = Write("settings.toml", toml) });

    private static IConfiguration Read(TomlConfigurationSource source)
    {
        source.ResolveFileProvider();
        return new ConfigurationBuilder().Add(source).Build();
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(directory.Path, name);
        File.WriteAllText(path, content);
        return path;
    }
}
