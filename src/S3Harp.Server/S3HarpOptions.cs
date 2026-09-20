using System.Globalization;

namespace S3Harp.Server;

/// <summary>
/// The settings S3Harp starts with. Each has one name, such as <c>data_dir</c>,
/// which is the suffix of its <c>S3HARP_</c> environment variable and the name
/// of its command-line flag. A setting that fails validation stops the server
/// before it listens with a <see cref="StartupException"/> naming it.
/// </summary>
internal sealed class S3HarpOptions
{
    /// <summary>The port clients connect to; 0 lets the system choose a free one.</summary>
    public const int DefaultPort = 9000;

    [ConfigurationKeyName(Keys.AccessKeyId)]
    public string AccessKeyId { get; set; } = "";

    [ConfigurationKeyName(Keys.SecretAccessKey)]
    public string SecretAccessKey { get; set; } = "";

    [ConfigurationKeyName(Keys.DataDirectory)]
    public string DataDirectory { get; set; } = "";

    /// <summary>The domain buckets are addressed under in virtual-hosted style.</summary>
    [ConfigurationKeyName(Keys.Domain)]
    public string Domain { get; set; } = ServiceDomain.Default.Name;

    /// <summary>The address to listen on: loopback unless told otherwise.</summary>
    [ConfigurationKeyName(Keys.Bind)]
    public string Bind { get; set; } = "127.0.0.1";

    [ConfigurationKeyName(Keys.Port)]
    public int Port { get; set; } = DefaultPort;

    /// <summary>The URL Kestrel listens on, with an IPv6 bind address bracketed.</summary>
    public string ListenUrl =>
        Bind.Contains(':', StringComparison.Ordinal)
            ? $"http://[{Bind}]:{Port.ToString(CultureInfo.InvariantCulture)}"
            : $"http://{Bind}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Binds the settings from configuration and validates them, reporting every
    /// problem at once by the setting it concerns.
    /// </summary>
    public static S3HarpOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new S3HarpOptions();
        try
        {
            configuration.Bind(options);
        }
        catch (InvalidOperationException exception)
        {
            // The binder names the configuration key whose value it could not convert.
            throw new StartupException(
                $"S3Harp cannot start: {DescribeConversionFailure(exception)}",
                exception
            );
        }

        var problems = new List<string>();
        Require(problems, Keys.AccessKeyId, options.AccessKeyId);
        Require(problems, Keys.SecretAccessKey, options.SecretAccessKey);
        Require(problems, Keys.DataDirectory, options.DataDirectory);
        Require(problems, Keys.Domain, options.Domain);
        Require(problems, Keys.Bind, options.Bind);
        if (options.Port is < 0 or > 65535)
        {
            problems.Add($"{Keys.Port} must be between 0 and 65535");
        }

        return problems.Count == 0
            ? options
            : throw new StartupException(
                "S3Harp cannot start: " + string.Join("; ", problems) + "."
            );
    }

    private static void Require(List<string> problems, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{key} is required");
        }
    }

    private static string DescribeConversionFailure(InvalidOperationException exception)
    {
        foreach (var key in Keys.All)
        {
            if (exception.Message.Contains($"'{key}'", StringComparison.OrdinalIgnoreCase))
            {
                return $"{key} must be a number.";
            }
        }

        return exception.Message;
    }

    /// <summary>The setting names, which are also the configuration keys.</summary>
    private static class Keys
    {
        public const string AccessKeyId = "access_key_id";
        public const string SecretAccessKey = "secret_access_key";
        public const string DataDirectory = "data_dir";
        public const string Domain = "domain";
        public const string Bind = "bind";
        public const string Port = "port";

        public static readonly string[] All =
        [
            AccessKeyId,
            SecretAccessKey,
            DataDirectory,
            Domain,
            Bind,
            Port,
        ];
    }
}
