using Microsoft.Extensions.Configuration;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3HarpOptionsTests
{
    private static readonly Dictionary<string, string?> Complete = new()
    {
        ["access_key_id"] = "S3HARPEXAMPLEKEY",
        ["secret_access_key"] = "secret",
        ["data_dir"] = "/tmp/s3harp-data",
    };

    [Fact]
    public void ReadsTheSettingsByTheirNames()
    {
        var options = S3HarpOptions.Load(
            Configuration(Complete, ("domain", "s3.test"), ("bind", "0.0.0.0"), ("port", "9010"))
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
            Configuration(Complete, ("bind", "::1"), ("port", "9000"))
        );

        Assert.Equal("http://[::1]:9000", options.ListenUrl);
    }

    [Theory]
    [InlineData("access_key_id")]
    [InlineData("secret_access_key")]
    [InlineData("data_dir")]
    public void RefusesToStartWithoutARequiredSetting(string missing)
    {
        var settings = new Dictionary<string, string?>(Complete) { [missing] = " " };

        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(settings))
        );

        Assert.Equal($"S3Harp cannot start: {missing} is required.", exception.Message);
    }

    [Fact]
    public void ListensOverTlsWhenACertificateAndKeyAreGiven()
    {
        var options = S3HarpOptions.Load(
            Configuration(
                Complete,
                ("tls_cert", "/etc/s3harp/fullchain.pem"),
                ("tls_key", "/etc/s3harp/privkey.pem")
            )
        );

        Assert.Equal("/etc/s3harp/fullchain.pem", options.TlsCert);
        Assert.Equal("/etc/s3harp/privkey.pem", options.TlsKey);
        Assert.True(options.UsesTls);
        Assert.Equal("https://127.0.0.1:9000", options.ListenUrl);
    }

    [Fact]
    public void ListensOverPlainHttpWithoutThem()
    {
        var options = S3HarpOptions.Load(Configuration(Complete));

        Assert.False(options.UsesTls);
        Assert.Equal("http://127.0.0.1:9000", options.ListenUrl);
    }

    [Theory]
    [InlineData("tls_cert")]
    [InlineData("tls_key")]
    public void RefusesACertificateOrKeyWithoutTheOther(string given)
    {
        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(Complete, (given, "/etc/s3harp/one.pem")))
        );

        Assert.Equal(
            "S3Harp cannot start: tls_cert and tls_key must be given together.",
            exception.Message
        );
    }

    [Theory]
    [InlineData("65536", "port must be between 0 and 65535")]
    [InlineData("-1", "port must be between 0 and 65535")]
    [InlineData("many", "port must be a number")]
    public void RefusesAPortOutsideTheRange(string port, string problem)
    {
        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(Complete, ("port", port)))
        );

        Assert.Equal($"S3Harp cannot start: {problem}.", exception.Message);
    }

    [Fact]
    public void PortZeroLetsTheSystemChoose() =>
        Assert.Equal(0, S3HarpOptions.Load(Configuration(Complete, ("port", "0"))).Port);

    [Fact]
    public void ReportsEveryProblemAtOnceByTheSettingItConcerns()
    {
        var exception = Assert.Throws<StartupException>(() =>
            S3HarpOptions.Load(Configuration(new Dictionary<string, string?>(), ("port", "70000")))
        );

        Assert.Equal(
            "S3Harp cannot start: access_key_id is required; secret_access_key is required; "
                + "data_dir is required; port must be between 0 and 65535.",
            exception.Message
        );
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
