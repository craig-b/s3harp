using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>
/// A 304 for a conditional GET or HEAD whose object is unchanged: the entity
/// headers a client needs to confirm its cached copy, and no body.
/// </summary>
internal sealed class S3NotModifiedResult(ObjectRecord record) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status304NotModified;
        response.Headers.ETag = $"\"{record.ETag}\"";
        response.Headers.LastModified = HttpDate.Format(record.LastModified);
        return Task.CompletedTask;
    }
}
