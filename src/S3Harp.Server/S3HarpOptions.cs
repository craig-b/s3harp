using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;

namespace S3Harp.Server;

/// <summary>
/// The settings S3Harp starts with, each named by its environment variable
/// without the <c>S3HARP_</c> prefix. The same names work as command-line
/// switches. A setting that fails validation stops the server before it listens
/// with a <see cref="StartupException"/> naming it.
/// </summary>
public sealed class S3HarpOptions
{
    private const string EnvironmentPrefix = "S3HARP_";

    /// <summary>The port clients connect to; 0 lets the system choose a free one.</summary>
    public const int DefaultPort = 9000;

    [ConfigurationKeyName("ACCESS_KEY_ID")]
    [Required(ErrorMessage = "is required")]
    public string AccessKeyId { get; init; } = "";

    [ConfigurationKeyName("SECRET_ACCESS_KEY")]
    [Required(ErrorMessage = "is required")]
    public string SecretAccessKey { get; init; } = "";

    [ConfigurationKeyName("DATA_DIR")]
    [Required(ErrorMessage = "is required")]
    public string DataDirectory { get; init; } = "";

    /// <summary>The domain buckets are addressed under in virtual-hosted style.</summary>
    [ConfigurationKeyName("DOMAIN")]
    [Required(ErrorMessage = "is required")]
    public string Domain { get; init; } = ServiceDomain.Default.Name;

    /// <summary>The address to listen on: loopback unless told otherwise.</summary>
    [ConfigurationKeyName("BIND")]
    [Required(ErrorMessage = "is required")]
    public string Bind { get; init; } = "127.0.0.1";

    [ConfigurationKeyName("PORT")]
    [Range(0, 65535, ErrorMessage = "must be between 0 and 65535")]
    public int Port { get; init; } = DefaultPort;

    /// <summary>The URL Kestrel listens on, with an IPv6 bind address bracketed.</summary>
    public string ListenUrl =>
        Bind.Contains(':', StringComparison.Ordinal)
            ? $"http://[{Bind}]:{Port.ToString(CultureInfo.InvariantCulture)}"
            : $"http://{Bind}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Binds the settings from configuration and validates them, reporting every
    /// problem at once by the environment variable it concerns.
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
            // The binder names the property whose value it could not convert.
            throw new StartupException(
                $"S3Harp cannot start: {DescribeConversionFailure(exception)}",
                exception
            );
        }

        var results = new List<ValidationResult>();
        if (
            Validator.TryValidateObject(
                options,
                new ValidationContext(options),
                results,
                validateAllProperties: true
            )
        )
        {
            return options;
        }

        var problems = results.Select(result =>
            $"{EnvironmentName(result.MemberNames.First())} {result.ErrorMessage}"
        );
        throw new StartupException("S3Harp cannot start: " + string.Join("; ", problems) + ".");
    }

    /// <summary>The binder names the configuration key whose value it could not convert.</summary>
    private static string DescribeConversionFailure(InvalidOperationException exception)
    {
        var property = typeof(S3HarpOptions)
            .GetProperties()
            .FirstOrDefault(p =>
                exception.Message.Contains($"'{KeyName(p)}'", StringComparison.OrdinalIgnoreCase)
            );
        return property is null
            ? exception.Message
            : $"{EnvironmentPrefix}{KeyName(property)} must be a number.";
    }

    /// <summary>The environment variable behind a property, such as <c>S3HARP_PORT</c>.</summary>
    private static string EnvironmentName(string propertyName) =>
        EnvironmentPrefix + KeyName(typeof(S3HarpOptions).GetProperty(propertyName)!);

    private static string KeyName(PropertyInfo property) =>
        property.GetCustomAttribute<ConfigurationKeyNameAttribute>()!.Name;
}
