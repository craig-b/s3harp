using System.CommandLine;
using System.Globalization;

namespace S3Harp.Server;

/// <summary>
/// The <c>s3harp</c> command line: one flag per setting, named after it, plus
/// the help and version the parser provides. Flags the user gives override the
/// environment.
/// </summary>
internal static class S3HarpCommand
{
    private static readonly Option<string> Config = Setting(
        "--config",
        SettingsFile.Key,
        "A settings file to read, JSON or TOML by its extension; the environment and flags override it."
    );

    private static readonly Option<string> AccessKeyId = Setting(
        "--access-key-id",
        "access_key_id",
        "The access key id clients sign requests with."
    );

    private static readonly Option<string> SecretAccessKey = Setting(
        "--secret-access-key",
        "secret_access_key",
        "The secret key matching the access key id."
    );

    private static readonly Option<string> DataDirectory = Setting(
        "--data-dir",
        "data_dir",
        "The directory holding all stored data, including the metadata index."
    );

    private static readonly Option<string> Bind = Setting(
        "--bind",
        "bind",
        "The address to listen on; 127.0.0.1 unless given, 0.0.0.0 serves other machines."
    );

    private static readonly Option<int> Port = Setting<int>(
        "--port",
        "port",
        $"The port to listen on; {S3HarpOptions.DefaultPort} unless given, 0 lets the system choose."
    );

    private static readonly Option<string> Domain = Setting(
        "--domain",
        "domain",
        "The domain buckets are addressed under in virtual-hosted style; localhost unless given."
    );

    private static readonly Option<string> TlsCert = Setting(
        "--tls-cert",
        "tls_cert",
        "The PEM file holding the server certificate and its chain; with --tls-key, the server listens over TLS."
    );

    private static readonly Option<string> TlsKey = Setting(
        "--tls-key",
        "tls_key",
        "The PEM file holding the certificate's private key."
    );

    private static readonly (Option Option, string Key)[] Settings_ =
    [
        (Config, SettingsFile.Key),
        (AccessKeyId, "access_key_id"),
        (SecretAccessKey, "secret_access_key"),
        (DataDirectory, "data_dir"),
        (Bind, "bind"),
        (Port, "port"),
        (Domain, "domain"),
        (TlsCert, "tls_cert"),
        (TlsKey, "tls_key"),
    ];

    /// <summary>The root command, whose action starts the server with the settings the flags give.</summary>
    public static RootCommand Create(Func<IReadOnlyDictionary<string, string?>, int> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var command = new RootCommand(
            "S3Harp: an S3-compatible object store. Every flag can also be set through the "
                + "environment variable it names."
        );
        foreach (var (option, _) in Settings_)
        {
            command.Options.Add(option);
        }

        command.SetAction(parseResult => start(Settings(parseResult)));
        return command;
    }

    /// <summary>The settings the command line gives, keyed by setting name; flags left out are absent.</summary>
    public static IReadOnlyDictionary<string, string?> Settings(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (option, key) in Settings_)
        {
            if (parseResult.GetResult(option) is { Implicit: false } result)
            {
                settings[key] = Convert.ToString(
                    result.GetValueOrDefault<object>(),
                    CultureInfo.InvariantCulture
                );
            }
        }

        return settings;
    }

    private static Option<T> Setting<T>(string flag, string key, string description) =>
        new(flag) { Description = $"{description} Environment: S3HARP_{key.ToUpperInvariant()}." };

    private static Option<string> Setting(string flag, string key, string description) =>
        Setting<string>(flag, key, description);
}
