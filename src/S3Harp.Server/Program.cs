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

builder.Services.AddSingleton(new RootCredentials(accessKeyId, secretAccessKey));
builder.Services.AddSingleton<ICredentialStore, RootCredentialStore>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.UseMiddleware<SigV4AuthenticationMiddleware>();
app.MapFallback(() => new S3ErrorResult(S3Errors.NotImplemented));

app.Run();
