using System.Text;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>
/// The composition root: builds a fully wired S3Harp server application. Settings
/// come from the file the <c>config</c> setting names, overridden by the
/// <c>S3HARP_</c> environment, overridden by any the caller passes, which is how
/// the command line reaches them. Only those sources name settings: an
/// environment variable without the prefix is not one.
/// </summary>
internal static partial class S3HarpApplication
{
    private const string EnvironmentPrefix = "S3HARP_";
    private const string MetadataHeaderPrefix = "x-amz-meta-";

    /// <summary>The framework logs only warnings by default; S3Harp's own categories log information.</summary>
    private static readonly Dictionary<string, string?> LoggingDefaults = new()
    {
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:S3Harp"] = "Information",
    };

    /// <exception cref="StartupException">The settings are unusable; the message names the problem.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    public static WebApplication Build(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var builder = WebApplication.CreateBuilder();
        RemoveUnprefixedEnvironment(builder.Configuration.Sources);
        builder.Configuration.Sources.Insert(
            0,
            new MemoryConfigurationSource { InitialData = LoggingDefaults }
        );
        if (SettingsFilePath(settings) is { } settingsFile)
        {
            SettingsFile.Insert(builder.Configuration, 1, settingsFile);
        }

        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.ConfigureKestrel(kestrel =>
            // User metadata is UTF-8 on the wire, so those response headers
            // carry the bytes back; every other header stays ASCII-only.
            kestrel.ResponseHeaderEncodingSelector = name =>
                name.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8
                    : null
        );

        var options = S3HarpOptions.Load(builder.Configuration);
        builder.WebHost.UseUrls(options.ListenUrl);
        if (options.UsesTls)
        {
            var certificate = TlsCertificate.Load(options.TlsCert, options.TlsKey);
            builder.Services.AddSingleton(certificate);
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.ConfigureHttpsDefaults(https =>
                {
                    https.ServerCertificate = certificate.Leaf;
                    https.ServerCertificateChain = certificate.Chain;
                })
            );
        }

        var dataDirectory = options.DataDirectory;
        Directory.CreateDirectory(dataDirectory);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ServiceDomain(options.Domain.Trim()));
        builder.Services.AddSingleton(
            new RootCredentials(options.AccessKeyId, options.SecretAccessKey)
        );
        builder.Services.AddSingleton<ICredentialStore>(
            new CredentialStore(
                options
                    .Keys.Select(key => (key.AccessKeyId, key.SecretAccessKey))
                    .Prepend((options.AccessKeyId, options.SecretAccessKey))
                    .ToDictionary(
                        key => key.AccessKeyId,
                        key => key.SecretAccessKey,
                        StringComparer.Ordinal
                    )
            )
        );
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMetadataIndex>(
            new SqliteMetadataIndex(Path.Combine(dataDirectory, "s3harp.db"))
        );
        builder.Services.AddSingleton(new BlobStore(dataDirectory));
        builder.Services.AddSingleton(StorageLimits.S3);
        builder.Services.AddSingleton<StorageEngine>();
        builder.Services.AddSingleton<S3RequestDispatcher>();

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var logger = app.Logger;
            if (logger.IsEnabled(LogLevel.Information))
            {
                var summary = StartupSummary.Describe(options, app.Urls);
                Log.Started(logger, summary);
            }
        });

        app.UseMiddleware<SigV4AuthenticationMiddleware>();

        // The dispatcher is the router: every authenticated request terminates here.
        var dispatcher = app.Services.GetRequiredService<S3RequestDispatcher>();
        app.Run(async context =>
        {
            var result = await dispatcher.DispatchAsync(context).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        });

        return app;
    }

    /// <summary>
    /// The framework reads every environment variable into configuration; S3Harp
    /// reads only those with its prefix, so a stray <c>PORT</c> is not a setting.
    /// </summary>
    private static void RemoveUnprefixedEnvironment(IList<IConfigurationSource> sources)
    {
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            {
                sources.RemoveAt(i);
            }
        }
    }

    /// <summary>The settings file the environment or the caller names, or null when neither does.</summary>
    private static string? SettingsFilePath(IReadOnlyDictionary<string, string?> settings) =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvironmentPrefix)
            .AddInMemoryCollection(settings)
            .Build()[SettingsFile.Key];

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            EventName = "Started",
            Level = LogLevel.Information,
            Message = "{Summary}"
        )]
        public static partial void Started(ILogger logger, string summary);
    }
}
