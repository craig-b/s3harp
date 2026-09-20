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

    [ConfigurationKeyName(Names.AccessKeyId)]
    public string AccessKeyId { get; set; } = "";

    [ConfigurationKeyName(Names.SecretAccessKey)]
    public string SecretAccessKey { get; set; } = "";

    [ConfigurationKeyName(Names.DataDirectory)]
    public string DataDirectory { get; set; } = "";

    /// <summary>The domain buckets are addressed under in virtual-hosted style.</summary>
    [ConfigurationKeyName(Names.Domain)]
    public string Domain { get; set; } = ServiceDomain.Default.Name;

    /// <summary>The address to listen on: loopback unless told otherwise.</summary>
    [ConfigurationKeyName(Names.Bind)]
    public string Bind { get; set; } = "127.0.0.1";

    [ConfigurationKeyName(Names.Port)]
    public int Port { get; set; } = DefaultPort;

    /// <summary>The PEM file holding the server certificate, followed by its chain when there is one.</summary>
    [ConfigurationKeyName(Names.TlsCert)]
    public string TlsCert { get; set; } = "";

    /// <summary>The PEM file holding the certificate's private key.</summary>
    [ConfigurationKeyName(Names.TlsKey)]
    public string TlsKey { get; set; } = "";

    /// <summary>Keypairs accepted besides the root pair, each with the same full access.</summary>
    [ConfigurationKeyName(Names.Keys)]
    public List<Keypair> Keys { get; set; } = [];

    /// <summary>Whether the server listens over TLS, which a certificate and key turn on.</summary>
    public bool UsesTls => !string.IsNullOrWhiteSpace(TlsCert);

    /// <summary>The URL Kestrel listens on, with an IPv6 bind address bracketed.</summary>
    public string ListenUrl
    {
        get
        {
            var scheme = UsesTls ? "https" : "http";
            var host = Bind.Contains(':', StringComparison.Ordinal) ? $"[{Bind}]" : Bind;
            return $"{scheme}://{host}:{Port.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>
    /// Binds the settings from configuration and validates them, reporting every
    /// problem at once by the setting it concerns.
    /// </summary>
    /// <exception cref="StartupException">The settings are unusable; the message names the problem.</exception>
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
        Require(problems, Names.AccessKeyId, options.AccessKeyId);
        Require(problems, Names.SecretAccessKey, options.SecretAccessKey);
        Require(problems, Names.DataDirectory, options.DataDirectory);
        Require(problems, Names.Domain, options.Domain);
        Require(problems, Names.Bind, options.Bind);
        if (options.Port is < 0 or > 65535)
        {
            problems.Add($"{Names.Port} must be between 0 and 65535");
        }

        if (string.IsNullOrWhiteSpace(options.TlsCert) != string.IsNullOrWhiteSpace(options.TlsKey))
        {
            problems.Add($"{Names.TlsCert} and {Names.TlsKey} must be given together");
        }

        var accessKeyIds = new HashSet<string>(StringComparer.Ordinal) { options.AccessKeyId };
        foreach (var (key, index) in options.Keys.Select((key, index) => (key, index)))
        {
            var entry = $"{Names.Keys}[{index.ToString(CultureInfo.InvariantCulture)}]";
            Require(problems, $"{entry}.{Names.AccessKeyId}", key.AccessKeyId);
            Require(problems, $"{entry}.{Names.SecretAccessKey}", key.SecretAccessKey);
            if (!string.IsNullOrWhiteSpace(key.AccessKeyId) && !accessKeyIds.Add(key.AccessKeyId))
            {
                problems.Add($"{entry} repeats the access key id {key.AccessKeyId}");
            }
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
        foreach (var key in Names.All)
        {
            if (exception.Message.Contains($"'{key}'", StringComparison.OrdinalIgnoreCase))
            {
                return $"{key} must be a number.";
            }
        }

        return exception.Message;
    }

    /// <summary>A keypair listed under <c>keys</c>: an access key id and its secret.</summary>
    internal sealed class Keypair
    {
        [ConfigurationKeyName(Names.AccessKeyId)]
        public string AccessKeyId { get; set; } = "";

        [ConfigurationKeyName(Names.SecretAccessKey)]
        public string SecretAccessKey { get; set; } = "";
    }

    /// <summary>The setting names, which are also the configuration keys.</summary>
    private static class Names
    {
        public const string AccessKeyId = "access_key_id";
        public const string SecretAccessKey = "secret_access_key";
        public const string DataDirectory = "data_dir";
        public const string Domain = "domain";
        public const string Bind = "bind";
        public const string Port = "port";
        public const string TlsCert = "tls_cert";
        public const string TlsKey = "tls_key";
        public const string Keys = "keys";

        public static readonly string[] All =
        [
            AccessKeyId,
            SecretAccessKey,
            DataDirectory,
            Domain,
            Bind,
            Port,
            TlsCert,
            TlsKey,
        ];
    }
}
