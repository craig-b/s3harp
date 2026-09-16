using System.Text;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>The composition root: builds a fully wired S3Harp server application.</summary>
public static class S3HarpApplication
{
    private const string MetadataHeaderPrefix = "x-amz-meta-";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("S3HARP_");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // User metadata is UTF-8 on the wire, so those response headers
            // carry the bytes back; every other header stays ASCII-only.
            kestrel.ResponseHeaderEncodingSelector = name =>
                name.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8
                    : null;
        });

        var accessKeyId = builder.Configuration["ACCESS_KEY_ID"];
        var secretAccessKey = builder.Configuration["SECRET_ACCESS_KEY"];
        if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(secretAccessKey))
        {
            throw new InvalidOperationException(
                "S3Harp requires credentials to start: set S3HARP_ACCESS_KEY_ID and S3HARP_SECRET_ACCESS_KEY.");
        }

        var dataDirectory = builder.Configuration["DATA_DIR"];
        if (string.IsNullOrEmpty(dataDirectory))
        {
            throw new InvalidOperationException(
                "S3Harp requires a data directory to start: set S3HARP_DATA_DIR.");
        }

        Directory.CreateDirectory(dataDirectory);

        builder.Services.AddSingleton(new RootCredentials(accessKeyId, secretAccessKey));
        builder.Services.AddSingleton<ICredentialStore, RootCredentialStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMetadataIndex>(
            new SqliteMetadataIndex(Path.Combine(dataDirectory, "s3harp.db")));
        builder.Services.AddSingleton(new BlobStore(dataDirectory));
        builder.Services.AddSingleton<StorageEngine>();
        builder.Services.AddSingleton<S3RequestDispatcher>();

        var app = builder.Build();

        app.UseMiddleware<SigV4AuthenticationMiddleware>();

        // The dispatcher is the router: every authenticated request terminates here.
        var dispatcher = app.Services.GetRequiredService<S3RequestDispatcher>();
        app.Run(async context =>
        {
            var result = await dispatcher.DispatchAsync(context);
            await result.ExecuteAsync(context);
        });

        return app;
    }
}
