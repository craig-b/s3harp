using S3Harp.Core;
using S3Harp.Server;
using S3Harp.Server.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("S3HARP_");

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

app.Run();
