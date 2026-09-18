using Microsoft.Extensions.Configuration;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3HarpOptionsTests
{
    private static readonly Dictionary<string, string?> Complete = new()
    {
        ["ACCESS_KEY_ID"] = "S3HARPEXAMPLEKEY",
        ["SECRET_ACCESS_KEY"] = "secret",
        ["DATA_DIR"] = "/tmp/s3harp-data",
    };

    [Fact]
    public void ReadsTheSettingsByTheirEnvironmentNames()
    {
        var options = S3HarpOptions.Load(
            Configuration(Complete, ("DOMAIN", "s3.test"), ("BIND", "0.0.0.0"), ("PORT", "9010"))
        );

        Assert.Equal("S3HARPEXAMPLEKEY", options.AccessKeyId);
        Assert.Equal("secret", options.SecretAccessKey);
        Assert.Equal("/tmp/s3harp-data", options.DataDirectory);
        Assert.Equal("s3.test", options.Domain);
        Assert.Equal("0.0.0.0", options.Bind);
        Assert.Equal(9010, options.Port);
    }

    [Fact]
    public void DefaultsToLoopbackPort9000UnderLocalhost()
    {
        var options = S3HarpOptions.Load(Configuration(Complete));

        Assert.Equal("localhost", options.Domain);
        Assert.Equal("127.0.0.1", options.Bind);
        Assert.Equal(9000, options.Port);
        Assert.Equal("http://127.0.0.1:9000", options.ListenUrl);
    }

    [Fact]
    public void BracketsAnIpv6BindAddressInTheListenUrl()
    {
        var options = S3HarpOptions.Load(
            Configuration(Complete, ("BIND", "::1"), ("PORT", "9000"))
        );

        Assert.Equal("http://[::1]:9000", options.ListenUrl);
    }

    [Theory]
    [InlineData("ACCESS_KEY_ID")]
    [InlineData("SECRET_ACCESS_KEY")]
    [InlineData("DATA_DIR")]
    public void RefusesToStartWithoutARequiredSetting(string missing)
    {
        var settings = new Dictionary<string, string?>(Complete) { [missing] = " " };

        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(settings))
        );

        Assert.Contains("S3HARP_" + missing, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("many")]
    public void RefusesAPortOutsideTheRange(string port)
    {
        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(Complete, ("PORT", port)))
        );

        Assert.Contains("S3HARP_PORT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PortZeroLetsTheSystemChoose() =>
        Assert.Equal(0, S3HarpOptions.Load(Configuration(Complete, ("PORT", "0"))).Port);

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(new Dictionary<string, string?>(), ("PORT", "70000")))
        );

        Assert.Contains("S3HARP_ACCESS_KEY_ID", exception.Message, StringComparison.Ordinal);
        Assert.Contains("S3HARP_DATA_DIR", exception.Message, StringComparison.Ordinal);
        Assert.Contains("S3HARP_PORT", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?> settings,
        params (string Key, string Value)[] extra
    )
    {
        var all = new Dictionary<string, string?>(settings);
        foreach (var (key, value) in extra)
        {
            all[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }
}
