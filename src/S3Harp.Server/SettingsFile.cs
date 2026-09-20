using Microsoft.Extensions.Configuration.Json;
using S3Harp.Server.Configuration;

namespace S3Harp.Server;

/// <summary>
/// The settings file the <c>config</c> setting names, read in the format its
/// extension says. It is read only when named, and naming a file that cannot be
/// read stops the server with a <see cref="StartupException"/> saying why.
/// </summary>
internal static class SettingsFile
{
    public const string Key = "config";

    /// <summary>
    /// Reads the file into the configuration at <paramref name="index"/>, below
    /// every source that follows, so each of those overrides it.
    /// </summary>
    public static void Insert(IConfigurationBuilder configuration, int index, string path)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new StartupException(
                $"S3Harp cannot start: {Key} names {fullPath}, which does not exist."
            );
        }

        var (source, format) = Path.GetExtension(fullPath).ToUpperInvariant() switch
        {
            ".JSON" => (Json(fullPath), "JSON"),
            ".TOML" => (Toml(fullPath), "TOML"),
            _ => throw new StartupException(
                $"S3Harp cannot start: {Key} must name a .json or .toml file, not {fullPath}."
            ),
        };

        try
        {
            configuration.Sources.Insert(index, source);
        }
        catch (InvalidDataException exception)
        {
            throw new StartupException(
                $"S3Harp cannot start: {fullPath} is not valid {format}: {Innermost(exception).Message}",
                exception
            );
        }
    }

    /// <summary>The parser's own exception, which says where the file went wrong, under the provider's wrapping.</summary>
    private static Exception Innermost(Exception exception) =>
        exception.InnerException is { } inner ? Innermost(inner) : exception;

    private static FileConfigurationSource Json(string fullPath) =>
        Resolved(new JsonConfigurationSource { Path = fullPath });

    private static FileConfigurationSource Toml(string fullPath) =>
        Resolved(new TomlConfigurationSource { Path = fullPath });

    private static FileConfigurationSource Resolved(FileConfigurationSource source)
    {
        source.ResolveFileProvider();
        return source;
    }
}
