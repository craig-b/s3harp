using System.CommandLine;
using System.Globalization;

namespace S3Harp.Server;

/// <summary>
/// The <c>s3harp</c> command line: one flag per setting, each standing in for
/// the environment variable of the same name, plus the help and version the
/// parser provides. Flags the user gives override the environment.
/// </summary>
internal static class S3HarpCommand
{
    private static readonly Option<string> AccessKeyId = Setting(
        "--access-key-id",
        "ACCESS_KEY_ID",
        "The access key id clients sign requests with."
    );

    private static readonly Option<string> SecretAccessKey = Setting(
        "--secret-access-key",
        "SECRET_ACCESS_KEY",
        "The secret key matching the access key id."
    );

    private static readonly Option<string> DataDirectory = Setting(
        "--data-dir",
        "DATA_DIR",
        "The directory holding all stored data, including the metadata index."
    );

    private static readonly Option<string> Bind = Setting(
        "--bind",
        "BIND",
        "The address to listen on; 127.0.0.1 unless given, 0.0.0.0 serves other machines."
    );

    private static readonly Option<int> Port = Setting<int>(
        "--port",
        "PORT",
        $"The port to listen on; {S3HarpOptions.DefaultPort} unless given, 0 lets the system choose."
    );

    private static readonly Option<string> Domain = Setting(
        "--domain",
        "DOMAIN",
        "The domain buckets are addressed under in virtual-hosted style; localhost unless given."
    );

    private static readonly (Option Option, string Key)[] Settings_ =
    [
        (AccessKeyId, "ACCESS_KEY_ID"),
        (SecretAccessKey, "SECRET_ACCESS_KEY"),
        (DataDirectory, "DATA_DIR"),
        (Bind, "BIND"),
        (Port, "PORT"),
        (Domain, "DOMAIN"),
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
        new(flag) { Description = $"{description} Environment: S3HARP_{key}." };

    private static Option<string> Setting(string flag, string key, string description) =>
        Setting<string>(flag, key, description);
}
