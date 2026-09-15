namespace S3Harp.Server;

/// <summary>A bare status-code response.</summary>
public sealed class S3StatusResult(int statusCode) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
