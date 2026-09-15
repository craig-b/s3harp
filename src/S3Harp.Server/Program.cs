using S3Harp.Server;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapFallback(() => new S3ErrorResult(S3Errors.NotImplemented));

app.Run();
